using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, byte> unreadableSettings = new(StringComparer.Ordinal);
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
        BindInstanceCache(entry);
        return entry;
    }
    private void BindInstanceCache(TEntry entry) {
        if (entry.GetSourceCode() == LegacySettingSource.Instance && host.InstanceCacheKey is not null) entry.InstanceCacheKey = host.InstanceCacheKey;
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
    private static bool UsesAccountStore(TEntry entry) => IsAccountSetting(entry) ||
        entry.Encrypted && entry.GetSourceCode() == LegacySettingSource.Registry && entry.Key == "LfSkinSites";
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
        BindInstanceCache(entry);
        if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null) {
            host.ValidateInstanceConfiguration?.Invoke(instance); host.ValidateInstanceSettingWrite?.Invoke(instance, entry.Key);
        }
        string? value = VbString(entry.GetCache(instance));
        Log(null, $"保存设置：{entry.Key} {(entry.Encrypted || entry.Key == "Identify" ? "" : "= " + value)}{(instance is null ? "" : $"（实例：{host.InstancePath(instance)}）")}", LegacySettingsLogLevel.Trace);
        if (entry.Encrypted) {
            try { value = LegacySettingsCrypto.Encrypt(value ?? "", EncryptionKey(host.Identify())); }
            catch { throw new InvalidOperationException("加密设置失败：" + entry.Key); }
        }
        switch (entry.GetSourceCode()) {
            case LegacySettingSource.Normal:
                host.WriteIni("Setup", entry.Key, value);
                break;
            case LegacySettingSource.Registry:
                try { host.WriteRegistry("HKEY_CURRENT_USER\\Software\\" + RegistryFolderFor(entry), RegistryValueName(entry), value); }
                catch when (entry.Encrypted) { throw new InvalidOperationException("写入加密设置失败：" + entry.Key); }
                catch (Exception error) { Log(error, $"写入注册表失败（{entry.Key} → {value}）", LegacySettingsLogLevel.Warning, true); }
                break;
            case LegacySettingSource.Instance:
                if (instance is null) throw new Exception($"保存版本独立设置 {entry.Key} 时未提供目标版本");
                if (host.WriteInstanceIni is not null) host.WriteInstanceIni(instance, entry.Key, value);
                else host.WriteIni(host.InstancePath(instance) + "PCL\\Setup.ini", entry.Key, value);
                break;
        }
    }

    private static string EncryptionKey(string? identify) => string.IsNullOrWhiteSpace(identify)
        ? throw new InvalidOperationException("无法获取加密所需的设备标识。") : "PCL" + identify;

    public void Set(string key, object? value, TInstance? instance = null) {
        if (entries().TryGetValue(key, out TEntry? entry) && UsesAccountStore(entry)) {
            try { WriteAccountValues(new Dictionary<string, object?> { { key, value } }); }
            catch { Log(null, "保存账号设置失败，原有数据已保留。", LegacySettingsLogLevel.Error, true); }
            return;
        }
        SetCore(key, value, instance);
    }
    public void SetCore(string key, object? value, TInstance? instance) {
        TEntry entry = Find(key);
        bool changedCache = false;
        try {
            if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null) {
                host.ValidateInstanceConfiguration?.Invoke(instance); host.ValidateInstanceSettingWrite?.Invoke(instance, key);
            }
            value = Coerce(value, entry.Type);
            if (entry.GetCache(instance) is null) Get(key, instance);
            if (VbEqual(entry.GetCache(instance), value)) return;
            entry.Set(value, instance);
            changedCache = true;
            entry.Save(instance);
            // Persist before callbacks: VersionServerLogin and its peers read back here.
            entry.OnChanged?.Invoke(value);
        } catch (Exception error) {
            if (changedCache && entry.Encrypted) entry.ValueCache.Clear();
            else if (changedCache && entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null && host.InstanceCacheKey is not null)
                entry.ValueCache.TryRemove(entry.GetInstanceCacheKey(instance), out _);
            Log(entry.Encrypted ? null : error, $"设置设置项时出错（{key}）", LegacySettingsLogLevel.Error);
        }
    }
    public void SetSafe(string key, object? value, TInstance? instance = null) {
        TEntry entry = Find(key);
        if (entry.Encrypted) throw new InvalidOperationException("禁止写入加密设置项：" + key);
        Set(key, value, instance);
    }
    public object? Get(string key, TInstance? instance = null) {
        if (entries().TryGetValue(key, out TEntry? entry) && UsesAccountStore(entry)) {
            lock (accountSync()) return GetCore(key, instance);
        }
        return GetCore(key, instance);
    }
    public object? GetCore(string key, TInstance? instance, Func<TEntry, string?>? readRaw = null, string? deviceIdentify = null) {
        TEntry entry = Find(key);
        if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null) host.ValidateInstanceConfiguration?.Invoke(instance);
        object? value = entry.GetCache(instance);
        if (value is not null && !IsAccountSetting(entry)) return value;
        if (forcedSettings.TryGetValue(key, out object? forcedSetting)) {
            entry.Set(forcedSetting, instance);
            return forcedSetting;
        }
        bool valueConversionFailed = false;
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
                        gotValue = host.ReadInstanceIni is not null ? host.ReadInstanceIni(instance, key, defaultValue) : host.ReadIni(host.InstancePath(instance) + "PCL\\Setup.ini", key, defaultValue);
                        break;
                }
            }
            if (entry.Encrypted) {
                try {
                    gotValue = VbStringEqual(gotValue, defaultValue) ? VbString(entry.DefaultValue)
                        : LegacySettingsCrypto.Decrypt(gotValue!, "PCL" + encryptionIdentify);
                    unreadableSettings.TryRemove(key, out _);
                } catch {
                    // Account reads poll the backing store. Report each failure once until a read succeeds.
                    if (unreadableSettings.TryAdd(key, 0))
                        Log(null, "解密设置失败，保留原始密文：" + key, LegacySettingsLogLevel.WarningLogOnly);
                    gotValue = VbString(entry.DefaultValue);
                }
            }
            object converted;
            try { converted = Coerce(gotValue, entry.Type); }
            catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) {
                valueConversionFailed = true; throw;
            }
            entry.Set(converted, instance);
        } catch (Exception error) {
            if (entry.GetSourceCode() == LegacySettingSource.Instance && host.InstanceCacheKey is not null) {
                // A successfully read but malformed ordinary preference retains LF's default fallback.
                // A failed read/gate never becomes a default, and a handoff during conversion is checked
                // again before logging or publishing even that default into the owner's fixed namespace.
                if (!valueConversionFailed) throw;
                if (instance is not null) host.ValidateInstanceConfiguration?.Invoke(instance);
            }
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
        if (entries().TryGetValue(key, out TEntry? entry) && UsesAccountStore(entry)) {
            WriteAccountValues(new Dictionary<string, object?> { { key, entry.DefaultValue } });
            return;
        }
        ResetCore(key, instance);
    }
    public void ResetCore(string key, TInstance? instance) {
        TEntry entry = Find(key);
        bool changedCache = false;
        try {
            if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null) {
                host.ValidateInstanceConfiguration?.Invoke(instance); host.ValidateInstanceSettingWrite?.Invoke(instance, key);
            }
            if (entry.GetCache(instance) is null) Get(key, instance);
            if (VbEqual(entry.GetCache(instance), entry.DefaultValue) && !(entry.GetSourceCode() == LegacySettingSource.Registry && IsCompatibleSetting(entry))) return;
            entry.Set(entry.DefaultValue!, instance);
            changedCache = true;
            switch (entry.GetSourceCode()) {
                case LegacySettingSource.Normal: host.DeleteIni("Setup", key); break;
                case LegacySettingSource.Registry:
                    using (ILegacySettingsStore? store = host.OpenRegistry(RegistryFolderFor(entry), true, true)) ResetRegistryPreference(store!, entry);
                    break;
                case LegacySettingSource.Instance:
                    if (instance is null) throw new Exception($"重置版本设置 {key} 时未提供目标版本");
                    if (host.DeleteInstanceIni is not null) host.DeleteInstanceIni(instance, key);
                    else host.DeleteIni(host.InstancePath(instance) + "PCL\\Setup.ini", key);
                    break;
            }
            entry.OnChanged?.Invoke(entry.DefaultValue!);
        } catch (Exception error) {
            if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null && host.InstanceCacheKey is not null) {
                if (changedCache) entry.ValueCache.TryRemove(entry.GetInstanceCacheKey(instance), out _);
            } else entry.ValueCache.Clear();
            Log(error, $"重置设置项时出错（{key}）", LegacySettingsLogLevel.Error);
        }
    }
    public string? GetDefault(string key) => VbString(entries()[key].DefaultValue);
    public bool HasSaved(string key, TInstance? instance = null) {
        TEntry entry = entries()[key];
        BindInstanceCache(entry);
        if (entry.GetSourceCode() == LegacySettingSource.Instance && instance is not null) host.ValidateInstanceConfiguration?.Invoke(instance);
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
                return host.HasInstanceIni is not null ? host.HasInstanceIni(instance, key) : host.HasIni(host.InstancePath(instance) + "PCL\\Setup.ini", key);
        }
    }
}
