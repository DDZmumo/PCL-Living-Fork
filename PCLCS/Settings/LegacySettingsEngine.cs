using System;
using System.Collections.Generic;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;

/// <summary>Complete LF settings/cache/persistence lifecycle using the original records.</summary>
public sealed partial class LegacySettingsEngine<TInstance, TEntry, TPlan>
    where TInstance : class
    where TEntry : LegacySettingData<TInstance>
    where TPlan : LegacyAccountMigrationPlan {
    private readonly Func<Dictionary<string, TEntry>> entries;
    private readonly Func<object> accountSync;
    private readonly Func<TPlan> createPlan;
    private readonly LegacySettingsHost<TInstance> host;
    private static readonly Dictionary<string, object> forcedSettings = new() {
        { "UiHiddenPageLink", true }, { "UiHiddenSetupLink", true }
    };

    public LegacySettingsEngine(Func<Dictionary<string, TEntry>> entries, Func<object> accountSync,
        Func<TPlan> createPlan, LegacySettingsHost<TInstance> host) {
        this.entries = entries;
        this.accountSync = accountSync;
        this.createPlan = createPlan;
        this.host = host;
    }

    private TEntry Find(string key) {
        if (!entries().TryGetValue(key, out TEntry? entry)) throw new KeyNotFoundException("未找到设置项：" + key);
        return entry;
    }
    private static object Coerce(object? value, Type type) => Conversion.CTypeDynamic(value, type)!;
    private static string? VbString(object? value) => Conversions.ToString(value);
    private static bool VbEqual(object? left, object? right) => Operators.ConditionalCompareObjectEqual(left, right, false);
    private static bool VbStringEqual(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
    private void Log(Exception? error, string message, LegacySettingsLogLevel level, bool toast = false) => host.Log(error, message, level, toast);

    public static bool IsAccountSetting(TEntry entry) {
        if (entry.GetSourceCode() != LegacySettingSource.Registry) return false;
        if (entry.Key == "LoginType" || entry.Key == "CacheMsV2Migrated") return true;
        return entry.Encrypted && (entry.Key.StartsWith("Login", StringComparison.Ordinal) ||
            entry.Key.StartsWith("CacheMs", StringComparison.Ordinal) || entry.Key.StartsWith("CacheNide", StringComparison.Ordinal) ||
            entry.Key.StartsWith("CacheAuth", StringComparison.Ordinal));
    }
    // LF main50 isolates Microsoft-client credentials while preserving logical setting names.
    public static bool IsMicrosoftAccountSetting(TEntry entry) => entry.GetSourceCode() == LegacySettingSource.Registry &&
        (entry.Key == "LoginMsJson" || entry.Key.StartsWith("CacheMs", StringComparison.Ordinal));
    public static string RegistryValueName(TEntry entry) => entry.Key + (IsMicrosoftAccountSetting(entry) ? "_LF" : "");
    public static bool IsCompatibleSetting(TEntry entry) {
        if (entry.GetSourceCode() != LegacySettingSource.Registry || entry.Encrypted) return false;
        if (LfRegistryStorage.IsOwned(RegistryValueName(entry))) return false;
        if (entry.Key == "ToolLauncherUpdateIgnoredTag") return false;
        return entry.Key.StartsWith("Launch", StringComparison.Ordinal) || entry.Key.StartsWith("Tool", StringComparison.Ordinal) ||
            entry.Key.StartsWith("Ui", StringComparison.Ordinal) || entry.Key.StartsWith("SystemDebug", StringComparison.Ordinal) || entry.Key == "SystemSystemCache";
    }
    public string RegistryFolderFor(TEntry entry) => entry.GetSourceCode() == LegacySettingSource.Registry && LfRegistryStorage.IsOwned(RegistryValueName(entry))
        ? LfRegistryStorage.Folder
        : IsAccountSetting(entry) || entry.Key == "Identify" || IsCompatibleSetting(entry) ? "PCL" : host.RegistryFolder();
    public string? ReadRegistrySetting(TEntry entry, string? defaultValue) =>
        host.ReadRegistry("HKEY_CURRENT_USER\\Software\\" + RegistryFolderFor(entry), RegistryValueName(entry), null) ?? defaultValue;

    public void SaveEntry(TEntry entry, TInstance? instance = null) {
        string? value = VbString(entry.GetCache(instance));
        Log(null, $"保存设置：{entry.Key} {(entry.Encrypted || entry.Key == "Identify" ? "" : "= " + value)}{(instance is null ? "" : $"（实例：{host.InstancePath(instance)}）")}", LegacySettingsLogLevel.Trace);
        if (entry.Encrypted) {
            try { value = LegacySettingsCrypto.Encrypt(value ?? "", "PCL" + host.Identify()); }
            catch (Exception error) { Log(error, "加密设置失败：" + entry.Key, LegacySettingsLogLevel.Warning); }
        }
        switch (entry.GetSourceCode()) {
            case LegacySettingSource.Normal:
                host.WriteIni("Setup", entry.Key, value);
                break;
            case LegacySettingSource.Registry:
                try { host.WriteRegistry("HKEY_CURRENT_USER\\Software\\" + RegistryFolderFor(entry), RegistryValueName(entry), value); }
                catch (Exception error) { Log(error, $"写入注册表失败（{entry.Key} → {value}）", LegacySettingsLogLevel.Warning, true); }
                break;
            case LegacySettingSource.Instance:
                if (instance is null) throw new Exception($"保存版本独立设置 {entry.Key} 时未提供目标版本");
                host.WriteIni(host.InstancePath(instance) + "PCL\\Setup.ini", entry.Key, value);
                break;
        }
    }

    public void Set(string key, object? value, TInstance? instance = null) {
        if (entries().TryGetValue(key, out TEntry? entry) && IsAccountSetting(entry)) {
            try { WriteAccountValues(new Dictionary<string, object?> { { key, value } }); }
            catch { Log(null, "保存账号设置失败，原有数据已保留。", LegacySettingsLogLevel.Error, true); }
            return;
        }
        SetCore(key, value, instance);
    }
    public void SetCore(string key, object? value, TInstance? instance) {
        TEntry entry = Find(key);
        try {
            value = Coerce(value, entry.Type);
            if (entry.GetCache(instance) is null) Get(key, instance);
            if (VbEqual(entry.GetCache(instance), value)) return;
            entry.Set(value, instance);
            entry.Save(instance);
            // Persist before callbacks: VersionServerLogin and its peers read back here.
            entry.OnChanged?.Invoke(value);
        } catch (Exception error) { Log(error, $"设置设置项时出错（{key}）", LegacySettingsLogLevel.Error); }
    }
    public void SetSafe(string key, object? value, TInstance? instance = null) {
        TEntry entry = Find(key);
        if (entry.Encrypted) throw new InvalidOperationException("禁止写入加密设置项：" + key);
        Set(key, value, instance);
    }
    public object? Get(string key, TInstance? instance = null) {
        if (entries().TryGetValue(key, out TEntry? entry) && IsAccountSetting(entry)) {
            lock (accountSync()) return GetCore(key, instance);
        }
        return GetCore(key, instance);
    }
    public object? GetCore(string key, TInstance? instance, Func<TEntry, string?>? readRaw = null, string? deviceIdentify = null) {
        TEntry entry = Find(key);
        object? value = entry.GetCache(instance);
        if (value is not null && !IsAccountSetting(entry)) return value;
        if (forcedSettings.TryGetValue(key, out object? forcedSetting)) {
            entry.Set(forcedSetting, instance);
            return forcedSetting;
        }
        try {
            string? gotValue = null;
            string encryptionIdentify = deviceIdentify ?? host.Identify();
            string? defaultValue = entry.Encrypted ? LegacySettingsCrypto.Encrypt(VbString(entry.DefaultValue)!, "PCL" + encryptionIdentify) : VbString(entry.DefaultValue);
            if (readRaw is not null) gotValue = readRaw(entry) ?? defaultValue;
            else {
                switch (entry.GetSourceCode()) {
                    case LegacySettingSource.Normal: gotValue = host.ReadIni("Setup", key, defaultValue); break;
                    case LegacySettingSource.Registry: gotValue = ReadRegistrySetting(entry, defaultValue); break;
                    case LegacySettingSource.Instance:
                        if (instance is null) throw new Exception($"读取版本设置 {key} 时未提供目标版本");
                        gotValue = host.ReadIni(host.InstancePath(instance) + "PCL\\Setup.ini", key, defaultValue);
                        break;
                }
            }
            if (entry.Encrypted) {
                if (VbStringEqual(gotValue, defaultValue)) gotValue = VbString(entry.DefaultValue);
                else {
                    try { gotValue = LegacySettingsCrypto.Decrypt(gotValue!, "PCL" + encryptionIdentify); }
                    catch {
                        Log(null, "解密设置失败，保留原始密文：" + key, LegacySettingsLogLevel.Warning);
                        gotValue = VbString(entry.DefaultValue);
                    }
                }
            }
            entry.Set(Coerce(gotValue, entry.Type), instance);
        } catch (Exception error) {
            Log(error, "读取设置失败：" + key, LegacySettingsLogLevel.Error, true);
            entry.Set(Coerce(entry.DefaultValue, entry.Type), instance);
        }
        return entry.GetCache(instance);
    }
    public object? GetSafe(string key, TInstance? instance = null) {
        TEntry entry = Find(key);
        if (entry.Encrypted) throw new InvalidOperationException("禁止读取加密设置项：" + key);
        return Get(key, instance);
    }
    public void Reset(string key, TInstance? instance = null) {
        if (entries().TryGetValue(key, out TEntry? entry) && IsAccountSetting(entry)) {
            WriteAccountValues(new Dictionary<string, object?> { { key, entry.DefaultValue } });
            return;
        }
        ResetCore(key, instance);
    }
    public void ResetCore(string key, TInstance? instance) {
        TEntry entry = Find(key);
        try {
            if (entry.GetCache(instance) is null) Get(key, instance);
            if (VbEqual(entry.GetCache(instance), entry.DefaultValue) && !(entry.GetSourceCode() == LegacySettingSource.Registry && IsCompatibleSetting(entry))) return;
            entry.Set(entry.DefaultValue!, instance);
            switch (entry.GetSourceCode()) {
                case LegacySettingSource.Normal: host.DeleteIni("Setup", key); break;
                case LegacySettingSource.Registry:
                    using (ILegacySettingsStore? store = host.OpenRegistry(RegistryFolderFor(entry), true, true)) ResetRegistryPreference(store!, entry);
                    break;
                case LegacySettingSource.Instance:
                    if (instance is null) throw new Exception($"重置版本设置 {key} 时未提供目标版本");
                    host.DeleteIni(host.InstancePath(instance) + "PCL\\Setup.ini", key);
                    break;
            }
            entry.OnChanged?.Invoke(entry.DefaultValue!);
        } catch (Exception error) {
            entry.ValueCache.Clear();
            Log(error, $"重置设置项时出错（{key}）", LegacySettingsLogLevel.Error);
        }
    }
    public string? GetDefault(string key) => VbString(entries()[key].DefaultValue);
    public bool HasSaved(string key, TInstance? instance = null) {
        TEntry entry = entries()[key];
        switch (entry.GetSourceCode()) {
            case LegacySettingSource.Normal: return host.HasIni("Setup", key);
            case LegacySettingSource.Registry:
                try {
                    if (host.HasRegistry("HKEY_CURRENT_USER\\Software\\" + RegistryFolderFor(entry), RegistryValueName(entry))) return true;
                    return ReadRegistrySetting(entry, null) is not null;
                } catch (Exception error) {
                    Log(error, $"判断注册表 {key} 是否存在时出错", LegacySettingsLogLevel.Warning);
                    return false;
                }
            default:
                if (instance is null) throw new Exception($"判断版本设置 {key} 是否存在时未提供目标版本");
                return host.HasIni(host.InstancePath(instance) + "PCL\\Setup.ini", key);
        }
    }
}
