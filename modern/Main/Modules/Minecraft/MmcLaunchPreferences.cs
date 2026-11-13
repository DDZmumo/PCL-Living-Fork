using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCL;

// Original LF host implementation; governed by the repository LICENCE.
public enum MmcLaunchPreferenceMode { InheritGlobal, UsePackage, Custom }
public sealed record MmcLaunchJavaPreference(MmcLaunchPreferenceMode Mode, string Path, bool Force) {
    public string VersionRange { get; init; }
    public bool InstanceFolderOnly { get; init; }
}
public sealed record MmcLaunchCustomCommand(string Command, bool WaitForExit) {
    public override string ToString() => "MMC user launch command [redacted]";
}
public sealed class MmcLaunchPreferenceSnapshot {
    public MmcRuntimeSettingsSnapshot RuntimeSettings { get; }
    public MmcLaunchJavaPreference JavaPreference { get; }
    public string SettingsFingerprint { get; }
    public bool MaximizeWindow { get; }
    public ImmutableArray<MmcLaunchCustomCommand> CustomCommands { get; }
    internal MmcConfiguration PackageConfiguration { get; }
    internal MmcLaunchPreferenceSnapshot(MmcRuntimeSettingsSnapshot runtime, MmcLaunchJavaPreference java, string fingerprint,
        bool maximize, ImmutableArray<MmcLaunchCustomCommand> commands, MmcConfiguration configuration) {
        RuntimeSettings = runtime; JavaPreference = java; SettingsFingerprint = fingerprint; MaximizeWindow = maximize;
        CustomCommands = commands; PackageConfiguration = configuration;
    }
    public override string ToString() => "MMC launch preference snapshot " + SettingsFingerprint;
}

public static class MmcLaunchPreferences {
    private static readonly string[] GlobalKeys = ["LaunchAdvanceJvm", "LaunchAdvanceGame", "LaunchAdvanceRun", "LaunchAdvanceRunWait",
        "LaunchAdvanceDisableJLW", "LaunchRamType", "LaunchRamCustom", "LaunchArgumentWindowType", "LaunchArgumentWindowWidth", "LaunchArgumentWindowHeight"];
    private static readonly string[] InstanceKeys = ["MmcJavaLocationMode", "MmcJvmArgsMode", "MmcMemoryMode", "MmcWindowMode", "VersionArgumentJavaSelect",
        "VersionArgumentJavaV2", "VersionArgumentJavaRange", "VersionAdvanceJvm", "VersionRamType", "VersionRamCustom", "VersionAdvanceGame", "VersionAdvanceRun",
        "VersionAdvanceRunWait", "VersionAdvanceDisableJLW", "MmcWindowWidth", "MmcWindowHeight", "MmcWindowFullscreen", "MmcWindowMaximized"];
    internal static Func<ulong> AvailableMemoryBytes { get; set; } = () => My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory;
    internal static Func<(int Width, int Height)> LauncherSize { get; set; } = () => ModBase.RunInUiWait(() => {
        if (ModMain.FrmMain is null) throw new InvalidOperationException("无法读取启动器窗口尺寸。");
        return ((int)Math.Round(ModMain.FrmMain.ActualWidth), (int)Math.Round(ModMain.FrmMain.ActualHeight - 29.5 * ModBase.DPI / 96));
    });

    /// <summary>Read only the Java preference path; settings previews need no RAM/window/UI or custom-command capture.</summary>
    internal static MmcLaunchJavaPreference CaptureJavaPreference(ModMinecraft.McInstance instance, MmcConfiguration packageConfiguration) {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(packageConfiguration);
        var view = MmcIniConfiguration.CapturePreferences(instance.Config);
        if (instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed)
            throw new IOException("MMC Java 偏好需要明确的管理实例。");
        return JavaPreference(packageConfiguration, view.Ini, view.Config, ImmutableDictionary<string, string>.Empty, Mode(view.Ini, "MmcJavaLocationMode"));
    }

    public static MmcLaunchPreferenceSnapshot Capture(ModMinecraft.McInstance instance, MmcConfiguration packageConfiguration, Guid sessionId) {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(packageConfiguration);
        if (sessionId == Guid.Empty) throw new ArgumentException("启动会话身份不能为空。", nameof(sessionId));
        var view = MmcIniConfiguration.CapturePreferences(instance.Config);
        if (instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed) throw new IOException("MMC 启动偏好需要明确的管理实例。");
        var global = Globals(cachedOnly: false); var ini = view.Ini;
        var javaMode = Mode(ini, "MmcJavaLocationMode"); var jvmMode = Mode(ini, "MmcJvmArgsMode");
        var memoryMode = Mode(ini, "MmcMemoryMode"); var windowMode = Mode(ini, "MmcWindowMode");
        string jvm = jvmMode switch {
            MmcLaunchPreferenceMode.InheritGlobal => global["LaunchAdvanceJvm"],
            MmcLaunchPreferenceMode.UsePackage => packageConfiguration.Values.GetValueOrDefault("JvmArgs", ""),
            _ => ini.GetValueOrDefault("VersionAdvanceJvm", "")
        };
        // This selects one preference string only. Required component +jvmArgs remain in the bound
        // Core plan and are appended by the runtime builder without joining/re-splitting/deduplication.
        var jvmTokens = Tokens(jvm, "JVM 参数");
        var memory = Memory(instance, packageConfiguration, ini, global, memoryMode);
        var window = Window(packageConfiguration, ini, global, windowMode);
        string game = ini.TryGetValue("VersionAdvanceGame", out string ownGame) ? ownGame : global["LaunchAdvanceGame"];
        var runtime = new MmcRuntimeSettingsSnapshot(instance.Name, "PCL Living Fork", ModBase.VersionDisplay,
            memory.Minimum, memory.Maximum, window.Width, window.Height, sessionId) {
            UserJvmTokens = jvmTokens, ExtraGameTokens = Tokens(game, "游戏参数"), Fullscreen = window.Fullscreen,
            // An active OverrideEnv/Env mapping is an ordered package requirement carried verbatim to the
            // managed process boundary. An inactive or absent mapping stays empty, so the child inherits
            // this launcher's base process environment unchanged.
            EnvironmentOverride = packageConfiguration.EnvironmentOverride,
            // The package wrapper is a capability separate from WrapperMode on the next line: it wraps the final
            // command line while the inner Java invocation keeps its JavaWrapper/DirectMain shape. Null means no
            // package wrapper is declared.
            WrapperCommand = MmcRuntimeWrapperCommand.Read(packageConfiguration),
            WrapperMode = Boolean(global["LaunchAdvanceDisableJLW"], "全局 JavaWrapper 设置") || Boolean(ini.GetValueOrDefault("VersionAdvanceDisableJLW", "False"), "实例 JavaWrapper 设置") ? MmcRuntimeWrapperMode.DirectMain : MmcRuntimeWrapperMode.JavaWrapper
        };
        var commands = ImmutableArray.CreateBuilder<MmcLaunchCustomCommand>();
        AddCommand(global["LaunchAdvanceRun"], Boolean(global["LaunchAdvanceRunWait"], "全局命令等待设置"));
        AddCommand(ini.GetValueOrDefault("VersionAdvanceRun", ""), Boolean(ini.GetValueOrDefault("VersionAdvanceRunWait", "True"), "实例命令等待设置"));
        return new(runtime, JavaPreference(packageConfiguration, ini, view.Config, global, javaMode), Fingerprint(instance, packageConfiguration, view, global),
            window.Maximized, commands.ToImmutable(), packageConfiguration);
        void AddCommand(string command, bool wait) {
            if (command.Length > 16384 || command.Contains('\0')) throw new InvalidDataException("自定义启动命令无效或过长。");
            if (!string.IsNullOrWhiteSpace(command)) commands.Add(new(command, wait));
        }
    }

    /// <summary>Fresh managed JSON/INI plus the captured application's global setting cache. Never dispatches UI or evaluates RAM/window measurements under admission.</summary>
    public static void Revalidate(ModMinecraft.McInstance instance, MmcLaunchPreferenceSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(snapshot);
        var current = MmcIniConfiguration.CapturePreferences(instance.Config);
        if (Fingerprint(instance, snapshot.PackageConfiguration, current, Globals(cachedOnly: true)) != snapshot.SettingsFingerprint)
            throw new IOException("启动偏好已变化，请重新准备本次启动。");
    }

    internal static bool TrySaveUserPreference(ModMinecraft.McInstance instance, string key, object value, bool reset) {
        if (instance is null || !ModeKey(key, out string modeKey) || instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed) return false;
        object coerced = reset ? Settings.Entries[key].DefaultValue : Settings.Entries[key].Type == typeof(int) ? Conversions.ToInteger(value) : Conversions.ToString(value) ?? "";
        string text = Convert.ToString(coerced, CultureInfo.InvariantCulture) ?? "";
        string mode = reset || key == "VersionRamType" && text == "2" || key == "VersionArgumentJavaSelect" && text == "使用全局设置" ? "InheritGlobal" : "Custom";
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string> { [key] = reset ? null : text, [modeKey] = mode });
        var entry = Settings.Entries[key]; entry.ValueCache.TryRemove(instance.PathVersion + instance.Config.PreferenceCacheNamespace, out _);
        entry.OnChanged?.Invoke(coerced);
        return true;
    }
    public static void SetForcedJava(ModMinecraft.McInstance instance, Java java) {
        if (instance.Config.ContextStamp.Kind != MmcConfigurationContextKind.Managed) {
            if (java is null) Configs.JavaForced.Reset(instance.Config); else Configs.JavaForced.Set(java, instance.Config);
            return;
        }
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string> {
            ["MmcJavaLocationMode"] = java is null ? "InheritGlobal" : "Custom", ["VersionArgumentJavaV2"] = java is null ? "0" : "3"
        }, new JObject { ["InstanceForcedJava"] = java is null ? JValue.CreateNull() : JObject.FromObject(java) });
        Settings.Entries["VersionArgumentJavaV2"].ValueCache.TryRemove(instance.PathVersion + instance.Config.PreferenceCacheNamespace, out _);
    }
    public static void SetWindowOverride(ModMinecraft.McInstance instance, int width, int height, bool fullscreen = false, bool maximized = false) {
        Size(width, height);
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string> {
            ["MmcWindowMode"] = "Custom", ["MmcWindowWidth"] = width.ToString(CultureInfo.InvariantCulture), ["MmcWindowHeight"] = height.ToString(CultureInfo.InvariantCulture),
            ["MmcWindowFullscreen"] = fullscreen.ToString(), ["MmcWindowMaximized"] = maximized.ToString()
        });
    }
    private static bool ModeKey(string key, out string mode) {
        mode = key switch { "VersionAdvanceJvm" => "MmcJvmArgsMode", "VersionRamType" or "VersionRamCustom" => "MmcMemoryMode",
            "VersionArgumentJavaSelect" or "VersionArgumentJavaV2" or "VersionArgumentJavaRange" => "MmcJavaLocationMode", _ => null };
        return mode is not null;
    }
    private static MmcLaunchJavaPreference JavaPreference(MmcConfiguration configuration, IReadOnlyDictionary<string, string> ini, JObject json,
        IReadOnlyDictionary<string, string> global, MmcLaunchPreferenceMode mode) {
        if (mode == MmcLaunchPreferenceMode.UsePackage) return new(mode, configuration.Values.GetValueOrDefault("JavaPath"), false);
        // The current global selector orders Configs.JavaList; only an instance's
        // explicit Custom/forced mode may restrict selection to one executable.
        if (mode == MmcLaunchPreferenceMode.InheritGlobal) return new(mode, null, false);
        int kind = Integer(ini.GetValueOrDefault("VersionArgumentJavaV2", "0"), "实例 Java 选择模式", 0, 3);
        if (kind == 1) {
            string range = Required(ini, "VersionArgumentJavaRange"); string error = InstanceJavaRangeValidation.Validate(range, "[", "]");
            if (error.Length != 0) throw new InvalidDataException("实例 Java 范围无效：" + error);
            return new(mode, null, false) { VersionRange = range };
        }
        if (kind == 2) return new(mode, null, false) { InstanceFolderOnly = true };
        if (kind == 3) {
            string path = json["InstanceForcedJava"] is JObject forced ? forced.Value<string>("Folder") : null;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("实例要求强制 Java，但没有有效的 InstanceForcedJava.Folder。");
            return new(mode, path, true);
        }
        return !ini.ContainsKey("VersionArgumentJavaV2") && ini.TryGetValue("VersionArgumentJavaSelect", out string legacy) && legacy.StartsWith("{", StringComparison.Ordinal) ? LegacyJavaPreference(mode, legacy) : new(mode, null, false);
    }
    private static MmcLaunchJavaPreference LegacyJavaPreference(MmcLaunchPreferenceMode mode, string text) {
        if (string.IsNullOrEmpty(text) || text == "使用全局设置") return new(mode, null, false);
        if (!text.StartsWith("{", StringComparison.Ordinal)) throw new InvalidDataException("指定 Java 配置无效，请重新选择。");
        try {
            string path = JObject.Parse(text).Value<string>("Path");
            return !string.IsNullOrWhiteSpace(path) ? new(mode, path, true) : throw new InvalidDataException("指定 Java 缺少路径。");
        } catch (JsonException error) { throw new InvalidDataException("指定 Java JSON 无效。", error); }
    }
    private static (int Minimum, int Maximum) Memory(ModMinecraft.McInstance instance, MmcConfiguration package,
        IReadOnlyDictionary<string, string> ini, IReadOnlyDictionary<string, string> global, MmcLaunchPreferenceMode mode) {
        if (mode == MmcLaunchPreferenceMode.UsePackage) {
            int minimum = Integer(Required(package.Values, "MinMemAlloc"), "包内最小内存", 0, 1048576);
            int maximum = Integer(Required(package.Values, "MaxMemAlloc"), "包内最大内存", 16, 1048576);
            if (minimum > maximum) throw new InvalidDataException("包内最小内存不能超过最大内存。");
            return (minimum, maximum);
        }
        int kind = mode == MmcLaunchPreferenceMode.Custom ? Integer(Required(ini, "VersionRamType"), "实例内存模式", 0, 2) : 2;
        if (kind == 2) kind = Integer(global["LaunchRamType"], "全局内存模式", 0, 1);
        string slider = mode == MmcLaunchPreferenceMode.Custom && ini.GetValueOrDefault("VersionRamType") != "2" ? ini.GetValueOrDefault("VersionRamCustom") : global["LaunchRamCustom"];
        double gib;
        if (kind == 1) gib = InstanceRamPolicy.Manual(Integer(slider ?? "", "自定义内存滑块", 0, 4096));
        else {
            var initialPaths = MmcInstancePaths.Resolve(instance.PathVersion);
            var definition = MmcSavedSourceStore.ReadPrimary(initialPaths).Definition;
            string mods = MmcInstancePaths.Resolve(instance.PathVersion, definition.Layout.GameRootLeaf).ResolveRelative(MmcPathRole.Game, "mods"); int? count = null;
            if (Directory.Exists(mods)) {
                int entries = 0, modCount = 0;
                foreach (string file in Directory.EnumerateFiles(mods)) { if (++entries > 100000) throw new IOException("模组目录超过内存建议统计上限。"); if (Path.GetExtension(file).ToLowerInvariant() is ".jar" or ".zip" or ".litemod") modCount++; }
                count = modCount;
            }
            gib = InstanceRamPolicy.Automatic(AvailableMemoryBytes() / 1073741824d, count, false);
        }
        int maximumMiB = checked((int)Math.Round(gib * 1024));
        if (maximumMiB is < 16 or > 1048576) throw new InvalidDataException("内存设置必须在 16–1048576 MiB 范围内。");
        return (0, maximumMiB);
    }
    private static (int Width, int Height, bool Fullscreen, bool Maximized) Window(MmcConfiguration package, IReadOnlyDictionary<string, string> ini,
        IReadOnlyDictionary<string, string> global, MmcLaunchPreferenceMode mode) {
        if (mode == MmcLaunchPreferenceMode.UsePackage) {
            int width = Integer(Required(package.Values, "MinecraftWinWidth"), "包内窗口宽度", 1, 32768), height = Integer(Required(package.Values, "MinecraftWinHeight"), "包内窗口高度", 1, 32768);
            return (width, height, false, Boolean(package.Values.GetValueOrDefault("LaunchMaximized", "false"), "包内最大化设置"));
        }
        if (mode == MmcLaunchPreferenceMode.Custom) return (Integer(Required(ini, "MmcWindowWidth"), "实例窗口宽度", 1, 32768), Integer(Required(ini, "MmcWindowHeight"), "实例窗口高度", 1, 32768),
            Boolean(ini.GetValueOrDefault("MmcWindowFullscreen", "false"), "实例全屏设置"), Boolean(ini.GetValueOrDefault("MmcWindowMaximized", "false"), "实例最大化设置"));
        int kind = Integer(global["LaunchArgumentWindowType"], "全局窗口模式", 0, 4);
        var size = kind == 2 ? LauncherSize() : kind == 3 ? (Integer(global["LaunchArgumentWindowWidth"], "全局窗口宽度", 1, 32768), Integer(global["LaunchArgumentWindowHeight"], "全局窗口高度", 1, 32768)) : (854, 480);
        Size(size.Item1, size.Item2); return (size.Item1, size.Item2, kind == 0, kind == 4);
    }
    private static Dictionary<string, string> Globals(bool cachedOnly) => GlobalKeys.ToDictionary(key => key, key => {
        object value = cachedOnly ? Settings.Entries[key].GetCache(null) : Settings.Get(key);
        return Convert.ToString(value ?? throw new IOException("全局启动设置缓存已失效，请重新准备启动。"), CultureInfo.InvariantCulture) ?? "";
    }, StringComparer.Ordinal);
    private static string Fingerprint(ModMinecraft.McInstance instance, MmcConfiguration package, MmcInstancePreferenceView view, IReadOnlyDictionary<string, string> globals) {
        var values = new List<string> { "PCLLF.MmcLaunchPreferences/v1", instance.Config.ContextStamp.InstanceId.ToString("N"), instance.Config.PreferenceCacheNamespace };
        foreach (string key in InstanceKeys) { values.Add(key); values.Add(view.Ini.TryGetValue(key, out string value) ? "present:" + value : "absent"); }
        values.Add(view.Config["InstanceForcedJava"]?.ToString(Formatting.None) ?? "absent");
        foreach (var pair in globals.OrderBy(pair => pair.Key, StringComparer.Ordinal).Concat(package.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))) { values.Add(pair.Key); values.Add(pair.Value); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value)))));
    }
    private static MmcLaunchPreferenceMode Mode(IReadOnlyDictionary<string, string> values, string key) => !values.TryGetValue(key, out string value) ? MmcLaunchPreferenceMode.InheritGlobal :
        Enum.TryParse(value, false, out MmcLaunchPreferenceMode mode) && Enum.IsDefined(mode) && mode.ToString() == value ? mode : throw new InvalidDataException("未知的 MMC 偏好模式：" + key);
    private static string Required(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out string value) ? value : throw new InvalidDataException("偏好覆盖缺少必要值：" + key);
    private static int Integer(string value, string label, int minimum, int maximum) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result >= minimum && result <= maximum ? result : throw new InvalidDataException(label + "超出范围或不是有效整数。");
    private static bool Boolean(string value, string label) => value.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => throw new InvalidDataException(label + "不是有效布尔值。") };
    private static ImmutableArray<string> Tokens(string value, string label) {
        if (value.Length > 65536) throw new InvalidDataException(label + "超过长度上限。");
        try { return MmcArgumentTokenizer.Tokenize(value); } catch (FormatException error) { throw new InvalidDataException(label + "引号或转义无效。", error); }
    }
    private static void Size(int width, int height) { if (width is < 1 or > 32768 || height is < 1 or > 32768) throw new InvalidDataException("窗口尺寸必须在 1–32768 范围内。"); }
}
