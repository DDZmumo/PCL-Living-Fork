using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Actual Main Settings facade -> actual Engine -> actual sealed Windows adapter.
// This family must be selected alone and never runs by default on a user profile.
internal static class RegistryDataChecks
{
    private static readonly string[] MicrosoftNames = ["LoginMsJson", "CacheMsOAuthRefresh", "CacheMsAccess", "CacheMsProfileJson", "CacheMsUuid", "CacheMsName", "CacheMsV2Migrated", "CacheMsV2OAuthRefresh", "CacheMsV2Access", "CacheMsV2ProfileJson", "CacheMsV2Uuid", "CacheMsV2Name", "CacheMsV2Expires"];
    internal static void Run(FixtureEnvironment fixture)
    {
        using RegistryNamespaceLease registry = new();
        using RegistryDataPlatformScope platform = new(registry);
        string identify = PCL.ModBase.Identify;
        Native.Check(identify == LegacySettingsCrypto.SourceIdentify(RegistryDataPlatformScope.Hardware, RegistryDataPlatformScope.Seed), "The real Main CurrentIdentify did not use the already-installed synthetic identity primitives.");
        Native.Check(Native.Static("PCL.Settings", "DesEncrypt", "synthetic-user", "") as string == "yVKVR2ckWDXyfsNYInb5MQ==", "Real Main DES facade lost the independently fixed LF wire-format vector.");
        // IDictionary's IEnumerable enumerator may yield generic KeyValuePair values;
        // this check needs only the real schema keys, whose Keys collection yields strings.
        var expected = Native.Entries.Keys.Cast<string>().Where(key => key == "LoginMsJson" || key.StartsWith("CacheMs", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Native.Check(expected.SetEquals(MicrosoftNames), "The genuine Main schema is not the thirteen reviewed Microsoft records.");
        AutomaticLfNamespaceUpgrade(registry, identify);
        RoundtripAndForeign(registry, identify);
        UpgradeAndLogout(registry, identify);
        NonMicrosoftImport(registry, identify);
        RejectionRollbackAndCancellation(registry, platform, identify);
        IniAndUnknownFields(fixture, registry, identify);
        Native.Check(registry.Official.GetValueKind("UnknownFutureRegistrySetting") == RegistryValueKind.MultiString &&
            ((string[])registry.Official.GetValue("UnknownFutureRegistrySetting")!).SequenceEqual(new[] { "future", "保留" }),
            "Unknown real Registry data was changed by account/settings operations.");
        platform.AssertScope(); fixture.AssertNoForbiddenIo();
        Console.WriteLine("PASS registry-data: actual Main Settings/CurrentIdentify/DES and Windows adapter; thirteen _LF values/kinds, protected foreign sentinels, dedicated-namespace upgrade/logout, other-account import, real rejected-plan restore, prewrite validation/cancellation, and INI/JSON unknown fields. Registry access was confined to newly acquired marker-owned roots.");
    }
    private static string Encrypt(string text, string identify) => (string)Native.Static("PCL.Settings", "DesEncrypt", text, "PCL" + identify)!;
    private static string Decrypt(object value, string identify) => (string)Native.Static("PCL.Settings", "DesDecrypt", (string)value, "PCL" + identify)!;
    private static void AutomaticLfNamespaceUpgrade(RegistryNamespaceLease registry, string identify) {
        string lf = Encrypt("lf-upgrade-session", identify), foreign = Encrypt("official-sentinel", identify);
        registry.Official.SetValue("CacheMsV2OAuthRefresh_LF", lf, RegistryValueKind.String);
        registry.Official.SetValue("CacheMsV2OAuthRefresh", foreign, RegistryValueKind.String);
        registry.Official.SetValue("UiLaunchMoreSettings", "True", RegistryValueKind.String);
        registry.Legacy.SetValue("ToolLauncherUpdateIgnoredTag", "v1.0.0", RegistryValueKind.String);
        using (RegistryDataPlatformScope firstAccess = new(registry)) {
            Native.Check(PCL.Settings.HasSaved("UiLaunchMoreSettings"), "First actual Main preference access did not perform automatic namespace migration.");
            Native.Check(registry.Official.GetValue("UiLaunchMoreSettings", null) is null && registry.Official.GetValue("CacheMsV2OAuthRefresh_LF", null) is null &&
                registry.Legacy.GetValue("ToolLauncherUpdateIgnoredTag", null) is null, "Automatic migration did not clean verified LF-only old values.");
            Native.Check((string?)registry.LfOnly.GetValue("CacheMsV2OAuthRefresh_LF") == lf && (string?)registry.LfOnly.GetValue("UiLaunchMoreSettings") == "True" &&
                (string?)registry.Official.GetValue("CacheMsV2OAuthRefresh") == foreign, "Automatic migration changed a cipher or official credential sentinel.");
            firstAccess.AssertScope();
        }
        foreach (string name in new[] { "CacheMsV2OAuthRefresh_LF", "UiLaunchMoreSettings", "ToolLauncherUpdateIgnoredTag" }) registry.LfOnly.DeleteValue(name, false);
    }
    private static Dictionary<string, object> AccountValues(string identifyTag) => new(StringComparer.Ordinal)
    {
        ["LoginMsJson"] = "{\"Fork\":\"fork-refresh\"}", ["CacheMsOAuthRefresh"] = "lf-old-refresh", ["CacheMsAccess"] = "lf-old-access",
        ["CacheMsProfileJson"] = "{\"id\":\"old\",\"future\":{\"keep\":[1,true,\"留\"]}}", ["CacheMsUuid"] = "legacy-uuid", ["CacheMsName"] = "LegacyName",
        ["CacheMsV2Migrated"] = true, ["CacheMsV2OAuthRefresh"] = identifyTag + "-refresh", ["CacheMsV2Access"] = identifyTag + "-access",
        ["CacheMsV2ProfileJson"] = "{\"id\":\"current\",\"future\":{\"keep\":[1,true,\"留\"]}}", ["CacheMsV2Uuid"] = "current-uuid", ["CacheMsV2Name"] = "CurrentName", ["CacheMsV2Expires"] = 1234567890L
    };
    private static void RoundtripAndForeign(RegistryNamespaceLease registry, string identify)
    {
        // Valid current-device ciphertext is deliberate: an accidental unsuffixed
        // read would yield a visible foreign account, not a harmless parse fallback.
        foreach (string key in MicrosoftNames)
        {
            object entry = Native.Entries[key]!;
            string foreign = key == "LoginMsJson" ? "{\"Foreign\":\"foreign-refresh\"}" : key.EndsWith("ProfileJson", StringComparison.Ordinal) ? "{\"id\":\"foreign\"}" : key == "CacheMsV2Expires" ? "42" : "foreign-" + key;
            registry.Official.SetValue(key, key == "CacheMsV2Migrated" ? "True" : Encrypt(foreign, identify), RegistryValueKind.String);
            Native.Check((string)Native.Static("PCL.Settings", "RegistryValueName", entry)! == key + "_LF", "Actual record lost its physical namespace.");
        }
        registry.Official.SetValue("UnknownFutureRegistrySetting", new[] { "future", "保留" }, RegistryValueKind.MultiString);
        var before = Snapshot(registry.Official);
        foreach (string key in MicrosoftNames)
        {
            Native.Check(!PCL.Settings.HasSaved(key), "Foreign existence selected as saved LF data.");
            Native.Check(Equals(PCL.Settings.Get(key), Native.Get(Native.Entries[key]!, "DefaultValue")), "Foreign shared credential selected by real Main.Get.");
        }
        var raw = (IDictionary)Native.Static("PCL.Settings", "ReadPclAccountRegistrySnapshot", registry.LfOnly, false)!;
        Native.Check(MicrosoftNames.All(key => !raw.Contains(key)), "The actual snapshot selected shared Microsoft values.");
        Dictionary<string, object> values = AccountValues("current");
        PCL.Settings.SaveAccountGroup(values);
        foreach (string key in MicrosoftNames)
        {
            string stored = key + "_LF";
            Native.Check(registry.LfOnly.GetValueKind(stored) == RegistryValueKind.String, "Actual Registry write changed the reviewed physical value kind.");
            string plain = key == "CacheMsV2Migrated" ? Convert.ToString(registry.LfOnly.GetValue(stored), CultureInfo.InvariantCulture)! : Decrypt(registry.LfOnly.GetValue(stored)!, identify);
            Native.Check(plain == Convert.ToString(values[key], CultureInfo.InvariantCulture), "Actual DES/Registry account roundtrip changed a value.");
            Native.Check(Equals(PCL.Settings.Get(key), values[key]), "Actual facade account readback selected a different value/type.");
        }
        AssertUnchanged(before, registry.Official, MicrosoftNames.Concat(new[] { "UnknownFutureRegistrySetting" }));
    }
    private static void UpgradeAndLogout(RegistryNamespaceLease registry, string identify)
    {
        var foreign = Snapshot(registry.Official);
        foreach (string key in MicrosoftNames.Where(key => key.StartsWith("CacheMsV2", StringComparison.Ordinal))) registry.LfOnly.DeleteValue(key + "_LF", false);
        Native.Static("PCL.Settings", "EnsureMicrosoftCacheUpgrade");
        Native.Check(PCL.Settings.Get<string>("CacheMsV2OAuthRefresh") == "lf-old-refresh" && PCL.Settings.Get<bool>("CacheMsV2Migrated"), "Actual upgrade did not select only the LF old-format cache.");
        var upgraded = Snapshot(registry.LfOnly);
        Native.Static("PCL.Settings", "EnsureMicrosoftCacheUpgrade");
        AssertSame(upgraded, Snapshot(registry.LfOnly));
        PCL.Settings.UpdateMicrosoftAccounts(accounts => { accounts.RemoveAll(); accounts["Keep"] = "keep-refresh"; accounts["OldUser"] = "old-refresh"; });
        var session = AccountValues("fresh").Where(item => item.Key.StartsWith("CacheMsV2", StringComparison.Ordinal) && item.Key != "CacheMsV2Migrated").ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        PCL.Settings.SaveMicrosoftSession(session, "OldUser");
        JObject list = JObject.Parse(PCL.Settings.Get<string>("LoginMsJson"));
        Native.Check((string?)list["Keep"] == "keep-refresh" && (string?)list["CurrentName"] == "fresh-refresh" && list["OldUser"] is null, "Real facade JSON rename lost an unrelated saved account.");
        var logout = MicrosoftNames.Where(key => key.StartsWith("CacheMsV2", StringComparison.Ordinal)).ToDictionary(key => key, key => Native.Get(Native.Entries[key]!, "DefaultValue")!, StringComparer.Ordinal);
        logout["LoginMsJson"] = "{}"; logout["CacheMsV2Migrated"] = true;
        PCL.Settings.SaveAccountGroup(logout);
        registry.LfOnly.DeleteValue("CacheMsV2Migrated_LF", false); // Exercise explicit empty V2 guard even without its marker.
        Native.Static("PCL.Settings", "EnsureMicrosoftCacheUpgrade");
        Native.Check(PCL.Settings.Get<string>("CacheMsV2OAuthRefresh") == "" && PCL.Settings.Get<string>("LoginMsJson") == "{}" && PCL.Settings.Get<bool>("CacheMsV2Migrated"), "Same-namespace old cache revived an explicit logout.");
        Native.Static("PCL.Settings", "ResetRegistryPreference", registry.LfOnly, Native.Entries["LoginMsJson"]);
        Native.Check(registry.LfOnly.GetValue("LoginMsJson_LF", null) is null, "Reset did not remove only the LF physical key.");
        AssertUnchanged(foreign, registry.Official, MicrosoftNames);
    }
    private static void NonMicrosoftImport(RegistryNamespaceLease registry, string identify)
    {
        const string legacySeed = "native-registry-private-seed";
        string legacyIdentify = LegacySettingsCrypto.SourceIdentify(RegistryDataPlatformScope.Hardware, legacySeed)!;
        registry.Official.SetValue("Identify", RegistryDataPlatformScope.Seed, RegistryValueKind.String);
        registry.Legacy.SetValue("Identify", legacySeed, RegistryValueKind.String);
        registry.Official.SetValue("LoginLegacyName", Encrypt("shared¨NativeOnly", identify), RegistryValueKind.String);
        registry.Legacy.SetValue("LoginLegacyName", Encrypt("LegacyOne¨Shared", legacyIdentify), RegistryValueKind.String);
        foreach (string key in MicrosoftNames)
        {
            registry.Legacy.SetValue(key, key == "CacheMsV2Migrated" ? "False" : Encrypt(key == "LoginMsJson" ? "{\"UnknownClient\":\"private-refresh\"}" : "private-" + key, legacyIdentify), RegistryValueKind.String);
            registry.OlderPrivate.SetValue(key, new byte[] { 0x41, 0x42, 0x00, 0xFF }, RegistryValueKind.Binary);
        }
        var shared = Snapshot(registry.Official); var legacy = Snapshot(registry.Legacy); var older = Snapshot(registry.OlderPrivate);
        var legacySnapshot = (IDictionary)Native.Static("PCL.Settings", "ReadPclAccountRegistrySnapshot", registry.Legacy, true)!;
        Native.Check(legacySnapshot.Contains("LoginLegacyName") && MicrosoftNames.All(key => !legacySnapshot.Contains(key)),
            "Real Windows-adapter legacy snapshot exposed unattributed private Microsoft entries.");
        Native.Static("PCL.Settings", "ImportCompatiblePclAccounts");
        Native.Check(PCL.Settings.Get<string>("LoginLegacyName") == "LegacyOne¨Shared¨NativeOnly", "Actual Main/Windows-adapter non-MS import failed.");
        Native.Check(registry.Legacy.GetValue("LoginLegacyName", null) is null, "Committed old non-MS source was not cleaned.");
        AssertUnchanged(shared, registry.Official, MicrosoftNames); AssertUnchanged(legacy, registry.Legacy, MicrosoftNames); AssertSame(older, Snapshot(registry.OlderPrivate));
        var once = Snapshot(registry.Official); var source = Snapshot(registry.Legacy);
        Native.Static("PCL.Settings", "ImportCompatiblePclAccounts");
        AssertSame(once, Snapshot(registry.Official)); AssertSame(source, Snapshot(registry.Legacy));
    }
    private static void RejectionRollbackAndCancellation(RegistryNamespaceLease registry, RegistryDataPlatformScope platform, string identify)
    {
        var before = Snapshot(registry.Official); int opens = platform.Opens, reads = platform.Reads, writes = platform.Writes, has = platform.Has;
        Throws<OperationCanceledException>(() => PCL.Settings.SaveAccountGroup(new Dictionary<string, object> { ["CacheMsV2Name"] = "canceled" }, () => true));
        Throws<OperationCanceledException>(() => PCL.Settings.SaveMicrosoftSession(new Dictionary<string, object>(), "", () => true));
        Native.Check(platform.Opens == opens && platform.Reads == reads && platform.Writes == writes && platform.Has == has, "Cancellation reached Registry platform primitives.");
        AssertSame(before, Snapshot(registry.Official));
        Throws<ArgumentException>(() => PCL.Settings.SaveAccountGroup(new Dictionary<string, object> { ["LoginMsJson"] = "{\"bad\":42}" }));
        Native.Check(platform.Opens == opens, "Public invalid JSON was not rejected before opening its transaction store.");
        AssertSame(before, Snapshot(registry.Official)); // Prewrite rejection, deliberately not described as rollback.

        registry.LfOnly.SetValue("CacheMsV2Name_LF", Encrypt("before-plan", identify), RegistryValueKind.ExpandString);
        registry.LfOnly.SetValue("CacheMsV2Migrated_LF", 0, RegistryValueKind.DWord);
        before = Snapshot(registry.LfOnly);
        object plan = Activator.CreateInstance(Native.Type("PCL.Settings+PclAccountMigrationPlan"), true)!;
        Native.Set(plan, "CurrentIdentify", identify);
        var values = (IDictionary<string, string>)Native.Get(plan, "Values")!;
        values["CacheMsV2Name"] = Encrypt("temporary-first-write", identify);
        values["LoginMsJson"] = Encrypt("{\"invalid\":42}", identify);
        values["CacheMsV2Migrated"] = "True";
        // Genuine Main Apply -> Windows adapter SetValue -> GetValue -> Decode.
        // This low-level plan differs from public Save's earlier validation: its
        // invalid second item is rejected during persisted readback verification.
        // There is no substituted store or claimed per-write callback counter.
        InvalidOperationException failure = Throws<InvalidOperationException>(() => Native.Static("PCL.Settings", "ApplyPclAccountMigration", plan, registry.LfOnly, null));
        Native.Check(failure.Message == "账号格式转换校验失败", "Unexpected actual plan failure stage.");
        AssertSame(before, Snapshot(registry.LfOnly));
        Native.Check(registry.LfOnly.GetValueKind("CacheMsV2Name_LF") == RegistryValueKind.ExpandString && registry.LfOnly.GetValueKind("CacheMsV2Migrated_LF") == RegistryValueKind.DWord, "Actual failed-plan recovery lost original Registry kinds.");
    }
    private static void IniAndUnknownFields(FixtureEnvironment fixture, RegistryNamespaceLease registry, string identify)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "PCL", "Setup.ini");
        Native.Check(Native.Full(file).StartsWith(Native.Full(fixture.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Real Setup.ini path escaped the GUID output.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Native.Check(!File.Exists(file), "Unexpected existing Setup.ini in the fresh copied output; refusing to adopt it.");
        const string unknown = "{\"keep\":{\"nested\":[1,true,\"留\"]},\"future\":\"colon:value\"}";
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true))) writer.Write("WindowWidth:925\r\nUnknownFutureSetting:" + unknown + "\r\n");
        PCL.ModBase.IniClearCache("Setup"); ClearCache("WindowWidth");
        Native.Check(PCL.Settings.Get<int>("WindowWidth") == 925, "Real Main did not decode the old BOM/colon INI sample.");
        PCL.Settings.Set("WindowWidth", 1042);
        PCL.ModBase.IniClearCache("Setup"); ClearCache("WindowWidth");
        Native.Check(PCL.Settings.Get<int>("WindowWidth") == 1042 && PCL.ModBase.ReadIni("Setup", "UnknownFutureSetting") == unknown, "Real settings writeback lost the unknown INI JSON value.");
        const string profile = "{\"id\":\"synthetic\",\"unknown_future\":{\"keep\":[1,true,\"留\"]}}";
        PCL.Settings.SaveAccountGroup(new Dictionary<string, object> { ["CacheMsV2ProfileJson"] = profile });
        Native.Check(PCL.Settings.Get<string>("CacheMsV2ProfileJson") == profile && registry.LfOnly.GetValue("CacheMsV2ProfileJson_LF") is string raw && Decrypt(raw, identify) == profile && registry.Official.GetValue("CacheMsV2ProfileJson_LF", null) is null, "Actual Registry/JSON writeback removed unknown profile members.");
    }
    private static void ClearCache(string key) => ((IDictionary)Native.Get(Native.Entries[key]!, "ValueCache")!).Clear();
    private sealed record Value(RegistryValueKind Kind, object Data);
    private static Dictionary<string, Value> Snapshot(RegistryKey key) => key.GetValueNames().ToDictionary(name => name, name => new Value(key.GetValueKind(name), key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!), StringComparer.Ordinal);
    private static bool EqualData(object first, object second) => first is byte[] a && second is byte[] b ? a.SequenceEqual(b) : first is string[] x && second is string[] y ? x.SequenceEqual(y) : Equals(first, second);
    private static void AssertUnchanged(Dictionary<string, Value> before, RegistryKey key, IEnumerable<string> names)
    {
        var after = Snapshot(key);
        foreach (string name in names) Native.Check(before.TryGetValue(name, out Value? value) && after.TryGetValue(name, out Value? current) && value.Kind == current.Kind && EqualData(value.Data, current.Data), "An owned foreign sentinel changed: " + name);
    }
    private static void AssertSame(Dictionary<string, Value> before, Dictionary<string, Value> after) => Native.Check(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out Value? value) && item.Value.Kind == value.Kind && EqualData(item.Value.Data, value.Data)), "Actual Registry values/kinds changed unexpectedly.");
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " from the genuine facade.");
    }
}
