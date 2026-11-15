using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;
#if !NETFRAMEWORK
using System.Runtime.Versioning;
#endif

namespace PCLCS;

/// <summary>Actual Windows Registry adapter; wrapping a caller's key never takes its ownership.</summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
public sealed class LegacySettingsRegistryStore : ILegacySettingsStore {
    private readonly RegistryKey key;
    private readonly bool ownsKey;
    public LegacySettingsRegistryStore(RegistryKey key, bool ownsKey = false) {
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        this.ownsKey = ownsKey;
    }
    public object? GetValue(string name, object? defaultValue = null, bool doNotExpandEnvironmentNames = false) =>
        key.GetValue(name, defaultValue, doNotExpandEnvironmentNames ? RegistryValueOptions.DoNotExpandEnvironmentNames : RegistryValueOptions.None);
    public RegistryValueKind GetValueKind(string name) => key.GetValueKind(name);
    public void SetValue(string name, object value, RegistryValueKind kind) => key.SetValue(name, value, kind);
    public void DeleteValue(string name, bool throwOnMissingValue) => key.DeleteValue(name, throwOnMissingValue);
    public void Dispose() { if (ownsKey) key.Dispose(); }

    public static ILegacySettingsStore? Open(string folder, bool writable, bool create) {
        RegistryKey? key = create ? Registry.CurrentUser.CreateSubKey("Software\\" + folder) : Registry.CurrentUser.OpenSubKey("Software\\" + folder, writable);
        return key is null ? null : new LegacySettingsRegistryStore(key, true);
    }

    public static List<string> IdentifyCandidates(string seed) {
        var result = new List<string>();
        using RegistryKey? hardware = Registry.LocalMachine.OpenSubKey("SYSTEM\\HardwareConfig", false);
        string? current = LegacySettingsCrypto.SourceIdentify(Convert.ToString(hardware?.GetValue("LastConfig", "Unknown"), CultureInfo.InvariantCulture), seed);
        if (current is not null) result.Add(current);
        if (hardware is not null) {
            foreach (string name in hardware.GetSubKeyNames()) {
                if (!Guid.TryParse(name, out _)) continue;
                string? historical = LegacySettingsCrypto.SourceIdentify(name, seed);
                if (historical is not null && !result.Contains(historical)) result.Add(historical);
            }
        }
        return result;
    }
}
