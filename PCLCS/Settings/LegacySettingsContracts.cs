using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PCLCS;

// Behavior source: LF Pages/PageSetup/Settings*.vb at
// f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 (repository LICENCE).
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 is the base source.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModSetup.cs and
// PCL.Core/App/Configuration/{ConfigItem,Storage/CatIniFileProvider}.cs
// were reviewed as C# boundary references: ModSetup uses the application
// Plain Craft Launcher 2/LICENCE; PCL.Core uses the root LICENSE / Apache-2.0.
// No CE profile, schema, provider, cache, key or implementation is imported.
public enum LegacySettingSource { Normal, Registry, Instance }
public enum LegacySettingsLogLevel { Trace, Info, Warning, Error }

/// <summary>One shared value/cache record inherited by the original Settings.Setting.</summary>
public abstract class LegacySettingData<TInstance> where TInstance : class {
    public string Key = null!;
    public bool Encrypted;
    public object? DefaultValue;
    public Action<object>? OnChanged;
    public Type Type = null!;
    public ConcurrentDictionary<string, object> ValueCache = new();
    // Optional immutable instance identity namespace. Disk paths remain unchanged.
    public Func<TInstance, string>? InstanceCacheKey;

    protected LegacySettingData(string key, object? value, bool encrypted, Action<object>? onChanged,
        Action<Exception, string> logInitializationError) {
        try {
            Key = key;
            DefaultValue = value;
            Encrypted = encrypted;
            Type = (value ?? "").GetType();
            OnChanged = onChanged;
        } catch (Exception error) {
            logInitializationError(error, "初始化设置项失败");
        }
    }

    // The old-name adapter owns the original Sources-typed field. These methods
    // read that same field instead of introducing a duplicate Source property.
    public abstract LegacySettingSource GetSourceCode();
    protected abstract string GetInstancePath(TInstance instance);
    public string GetInstanceCacheKey(TInstance instance) => InstanceCacheKey?.Invoke(instance) ?? GetInstancePath(instance);
    public object? GetCache(TInstance? instance) {
        string key = GetSourceCode() == LegacySettingSource.Instance ? GetInstanceCacheKey(instance!) : "";
        return ValueCache.TryGetValue(key, out object? value) ? value : null;
    }
    public void Set(object value, TInstance? instance) {
        string key = GetSourceCode() == LegacySettingSource.Instance ? GetInstanceCacheKey(instance!) : "";
        ValueCache[key] = value;
    }
    public abstract void Save(TInstance? instance = null);
}

public class LegacyAccountMigrationPlan {
    public Dictionary<string, string> Values = new(StringComparer.Ordinal);
    public List<string> DeleteLegacy = new();
    public string? CurrentIdentify;
}

/// <summary>The actual Registry operations, also used by isolated fault-injection stores.</summary>
public interface ILegacySettingsStore : IDisposable {
    object? GetValue(string name, object? defaultValue = null, bool doNotExpandEnvironmentNames = false);
    RegistryValueKind GetValueKind(string name);
    void SetValue(string name, object value, RegistryValueKind kind);
    void DeleteValue(string name, bool throwOnMissingValue);
}

/// <summary>LF platform primitives; no business rule, second cache or deferred storage.</summary>
public sealed class LegacySettingsHost<TInstance> where TInstance : class {
    public Func<TInstance, string> InstancePath = null!;
    public Func<string> Identify = null!;
    public Func<string> RegistryFolder = null!;
    public Func<string, string, string?, string?> ReadIni = null!;
    public Action<string, string, string?> WriteIni = null!;
    public Action<string, string> DeleteIni = null!;
    // Instance-specific hosts retain immutable configuration identity at the path-only INI boundary.
    // Validation is data-only; global/account storage and legacy test hosts keep their existing path.
    public Action<TInstance>? ValidateInstanceConfiguration;
    // Must derive from the owner's fixed stamp/attachment epoch, never the path's mutable current ID.
    public Func<TInstance, string>? InstanceCacheKey;
    public Action<TInstance, string>? ValidateInstanceSettingWrite;
    public Func<TInstance, string, string?, string?>? ReadInstanceIni;
    public Func<TInstance, string, bool>? HasInstanceIni;
    public Action<TInstance, string, string?>? WriteInstanceIni;
    public Action<TInstance, string>? DeleteInstanceIni;
    public Func<string, string, bool> HasIni = null!;
    public Func<string, string, string?, string?> ReadRegistry = null!;
    public Action<string, string, string?> WriteRegistry = null!;
    public Func<string, string, bool> HasRegistry = null!;
    public Func<string, bool, bool, ILegacySettingsStore?> OpenRegistry = null!;
    public Func<string, List<string>> IdentifyCandidates = null!;
    public Action<Exception?, string, LegacySettingsLogLevel, bool> Log = null!;
}
