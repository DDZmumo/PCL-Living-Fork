using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Microsoft.Win32;
using PCLCS;
using Engine = PCLCS.LegacySettingsEngine<SettingsMicrosoftIsolationTests.Instance, SettingsMicrosoftIsolationTests.Entry, PCLCS.LegacyAccountMigrationPlan>;
#pragma warning disable CA1416 // Only inert RegistryValueKind tags in an in-memory fixture. No Registry APIs.

internal static class SettingsMicrosoftIsolationTests {
    private static readonly List<Fixture> fixtures = new();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T) { return; }
        throw new Exception(message);
    }
    // LF main50 policy; each case runs the actual shared engine, crypto and schema.
    // This fixture never opens Registry or user profiles. Cases remain in settings.
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("settings main50 isolates all thirteen Microsoft names and preserves ordinary defaults", () => RunCase(NamesAndSchema));
        yield return ("settings main50 saves logs out resets and probes only LF Microsoft storage", () => RunCase(ReadWriteAndLogout));
        yield return ("settings main50 excludes unattributed private Microsoft credentials while migrating other accounts", () => RunCase(SnapshotAndImport));
        yield return ("settings main50 transaction write readback and cleanup faults restore exact values and kinds", () => RunCase(TransactionFailures));
        yield return ("settings main50 upgrades only same-namespace caches and cancels before storage", () => RunCase(LocalUpgradeAndCancel));
    }
    private static void RunCase(Action test) {
        fixtures.Clear();
        try {
            test();
            // A forbidden read must fail even if the production error path catches
            // the fixture exception and returns a default/existence result.
            foreach (Fixture fixture in fixtures)
                Check(fixture.Official.ForbiddenAccesses.Count == 0 && fixture.Legacy.ForbiddenAccesses.Count == 0,
                    "A forbidden Microsoft storage call was attempted, including caught errors");
        } finally { fixtures.Clear(); }
    }
    private static void NamesAndSchema() {
        Fixture f = new();
        var microsoft = f.Entries.Values.Where(Engine.IsMicrosoftAccountSetting).ToArray();
        Check(microsoft.Length == 13, "Expected all thirteen LF Microsoft definitions");
        foreach (Entry e in microsoft) Check(Engine.RegistryValueName(e) == e.Key + "_LF", "MS physical namespace: " + e.Key);
        foreach (string key in new[] { "Identify", "LoginType", "CacheAuthAccess", "CacheNidePass", "LaunchFolders", "UiLauncherTheme", "ToolLauncherUpdateIgnoredTag" })
            Check(Engine.RegistryValueName(f.Entries[key]) == key, "Unrelated key renamed: " + key);
        foreach (string key in new[] { "ToolLauncherUpdateSource", "ToolLauncherUpdateChannel" }) {
            Check((int)f.Entries[key].DefaultValue! == 0 && f.Entries[key].GetSourceCode() == LegacySettingSource.Normal, "New default/source: " + key);
        }
        Check((int)f.Entries["UiLauncherColorMode"].DefaultValue! == 1 && f.Entries["UiLauncherColorMode"].GetSourceCode() == LegacySettingSource.Normal, "Dark theme default/source");
        Check((int)f.Entries["UiPanelOpacity"].DefaultValue! == 850 && f.Entries["UiPanelOpacity"].GetSourceCode() == LegacySettingSource.Normal, "Panel opacity default/source");
        Check(!Engine.IsMicrosoftAccountSetting(new Entry("CacheMsFuture", "", LegacySettingSource.Normal, true, null)), "Normal setting should not gain registry namespace");
    }
    private static void ReadWriteAndLogout() {
        Fixture f = new();
        string foreign = Fixture.Enc("{\"Foreign\":\"foreign-refresh\"}");
        f.Official.Seed("LoginMsJson", foreign); f.Official.Seed("CacheMsV2OAuthRefresh", Fixture.Enc("foreign-session"));
        Check(!f.Engine.HasSaved("LoginMsJson"), "Unsuffixed foreign account made HasSaved true");
        Check((string)f.Engine.Get("LoginMsJson")! == "{}", "Foreign credential read");
        f.Engine.WriteAccountValues(new Dictionary<string, object?> {
            ["LoginMsJson"] = "{\"Fork\":\"fork-refresh\"}", ["CacheMsV2OAuthRefresh"] = "fork-session", ["CacheMsV2Migrated"] = true
        });
        Check(f.Engine.HasSaved("LoginMsJson"), "LF saved credential missing");
        Check((string)f.Engine.Get("LoginMsJson")! == "{\"Fork\":\"fork-refresh\"}", "LF credential readback");
        Check(f.Official.Writes.Last() == "CacheMsV2Migrated_LF", "Migration marker was not the final write");
        Check((string)f.Official.Values["LoginMsJson"]! == foreign, "Save changed foreign account");
        f.Engine.WriteAccountValues(new Dictionary<string, object?> {
            ["LoginMsJson"] = "{}", ["CacheMsV2OAuthRefresh"] = "", ["CacheMsV2Migrated"] = true
        });
        Check(Fixture.Dec(f.Official.Values["CacheMsV2OAuthRefresh_LF"]!) == "", "LF logout not persisted");
        Check(Fixture.Dec(f.Official.Values["CacheMsV2OAuthRefresh"]!) == "foreign-session", "Logout changed foreign session");
        var raw = f.Engine.ReadAccountRegistrySnapshot(f.Official);
        var stale = new Dictionary<string, string> { ["LoginMsJson"] = Fixture.Enc("{\"Stale\":\"old\"}") };
        var plan = f.Engine.PlanAccountMigration(raw, stale, new[] { Fixture.Id }, new[] { Fixture.Id }, Fixture.Id);
        Check(plan.Values.Count == 0 && !plan.DeleteLegacy.Contains("LoginMsJson"), "Stale private cache revived LF logout");
        Engine.ResetRegistryPreference(f.Official, f.Entries["LoginMsJson"]);
        Check(!f.Official.Values.ContainsKey("LoginMsJson_LF") && (string)f.Official.Values["LoginMsJson"]! == foreign, "Reset touched foreign namespace");
        f.Engine.SaveEntry(f.Entries["CacheMsV2Name"]);
        Check(f.Official.Writes.Last() == "CacheMsV2Name_LF", "Raw entry SaveEntry escaped mapping");
        Check(!f.Logs.Any(s => s.Contains("fork-refresh") || s.Contains("foreign-session")), "Credential entered logs");
    }
    private static void SnapshotAndImport() {
        Fixture f = new();
        f.Official.Seed("Identify", "native-seed");
        f.Official.Seed("LoginMsJson", Fixture.Enc("{\"Foreign\":\"secret\"}"));
        f.Legacy.Seed("Identify", "private-seed");
        f.Legacy.Seed("LoginMsJson", Fixture.Enc("{\"UnknownClient\":\"unattributed\"}"));
        f.Legacy.Seed("CacheMsV2OAuthRefresh", Fixture.Enc("unattributed-session"));
        f.Legacy.Seed("LoginLegacyName", Fixture.Enc("LegacyName"));
        var official = f.Engine.ReadAccountRegistrySnapshot(f.Official);
        var legacy = f.Engine.ReadAccountRegistrySnapshot(f.Legacy, true);
        Check(!official.ContainsKey("LoginMsJson") && !legacy.ContainsKey("LoginMsJson") && !legacy.ContainsKey("CacheMsV2OAuthRefresh"), "Snapshot imported foreign Microsoft data");
        Check(f.Engine.ReadAccountRegistrySnapshot(null).Count == 0, "Null legacy snapshot should be empty");
        f.Engine.ImportCompatibleAccounts();
        Check(Fixture.Dec(f.Official.Values["LoginLegacyName"]!) == "LegacyName", "Non-MS account compatibility lost");
        Check(!f.Legacy.Values.ContainsKey("LoginLegacyName"), "Committed non-MS legacy not cleaned");
        Check(f.Legacy.Values.ContainsKey("LoginMsJson") && f.Legacy.Values.ContainsKey("CacheMsV2OAuthRefresh"), "Unattributed private Microsoft data deleted");
        Check(!f.Official.Values.ContainsKey("LoginMsJson_LF"), "Unknown private Microsoft cache imported");
        var before = f.Official.Values.ToDictionary(x => x.Key, x => x.Value);
        f.Engine.ImportCompatibleAccounts();
        Check(before.Count == f.Official.Values.Count && before.All(x => Equals(x.Value, f.Official.Values[x.Key])), "Account migration not idempotent");
        Check(f.Legacy.Reads.All(key => key != "LoginMsJson" && !key.StartsWith("CacheMs", StringComparison.Ordinal)), "Private MS was read despite exclusion");
    }
    private static void TransactionFailures() {
        foreach (bool badReadback in new[] { false, true }) {
            Fixture f = new();
            f.Official.Seed("CacheMsV2OAuthRefresh_LF", Fixture.Enc("prior"), RegistryValueKind.ExpandString);
            f.Official.Seed("CacheMsV2Migrated_LF", 0, RegistryValueKind.DWord);
            var before = f.Official.Values.ToDictionary(x => x.Key, x => x.Value);
            var kinds = f.Official.Kinds.ToDictionary(x => x.Key, x => x.Value);
            if (badReadback) f.Official.BadReadbackOnce = "LoginMsJson_LF";
            else f.Official.FailWriteOnce = "LoginMsJson_LF";
            Throws<Exception>(() => f.Engine.WriteAccountValues(new Dictionary<string, object?> {
                ["CacheMsV2Migrated"] = true, ["CacheMsV2OAuthRefresh"] = "new-session", ["LoginMsJson"] = "{\"New\":\"refresh\"}"
            }), "Injected transaction failure did not propagate");
            Check(f.Official.FailWriteOnce is null && f.Official.BadReadbackOnce is null, "Expected fault stage was not reached");
            Check(before.Count == f.Official.Values.Count && before.All(x => Equals(x.Value, f.Official.Values[x.Key])), "Failure did not restore original values");
            Check(kinds.All(x => x.Value == f.Official.Kinds[x.Key]), "Failure did not restore RegistryValueKind");
            Check(!f.Official.Writes.TakeWhile(x => x != "LoginMsJson_LF").Contains("CacheMsV2Migrated_LF"), "Failed transaction published marker early");
        }
        Fixture cleanup = new();
        cleanup.Legacy.Seed("LoginLegacyName", Fixture.Enc("old"), RegistryValueKind.ExpandString);
        var plan = new LegacyAccountMigrationPlan { CurrentIdentify = Fixture.Id };
        plan.Values["LoginLegacyName"] = Fixture.Enc("new"); plan.DeleteLegacy.Add("LoginLegacyName");
        cleanup.Legacy.FailDeleteOnce = "LoginLegacyName";
        Throws<IOException>(() => cleanup.Engine.ApplyAccountMigration(plan, cleanup.Official, cleanup.Legacy), "Cleanup failure swallowed");
        Check(!cleanup.Official.Values.ContainsKey("LoginLegacyName") && Fixture.Dec(cleanup.Legacy.Values["LoginLegacyName"]!) == "old", "Cleanup fault rollback lost data");
        Check(cleanup.Legacy.Kinds["LoginLegacyName"] == RegistryValueKind.ExpandString, "Cleanup fault lost kind");
    }
    private static void LocalUpgradeAndCancel() {
        Fixture f = new();
        f.Official.Seed("CacheMsOAuthRefresh", Fixture.Enc("foreign"));
        f.Official.Seed("CacheMsOAuthRefresh_LF", Fixture.Enc("lf-old"));
        f.Official.Seed("CacheMsName_LF", Fixture.Enc("LF Name"));
        f.Engine.EnsureMicrosoftCacheUpgrade();
        Check(Fixture.Dec(f.Official.Values["CacheMsV2OAuthRefresh_LF"]!) == "lf-old", "Same-namespace old cache not upgraded");
        Check((string)f.Official.Values["CacheMsV2Migrated_LF"]! == bool.TrueString, "LF upgrade marker missing");
        Check(!f.Official.Values.ContainsKey("CacheMsV2OAuthRefresh"), "Upgrade wrote shared V2 session");
        int writes = f.Official.Writes.Count; f.Engine.EnsureMicrosoftCacheUpgrade();
        Check(f.Official.Writes.Count == writes, "Upgrade not idempotent");
        Fixture loggedOut = new(); loggedOut.Official.Seed("CacheMsOAuthRefresh_LF", Fixture.Enc("stale")); loggedOut.Official.Seed("CacheMsV2OAuthRefresh_LF", Fixture.Enc(""));
        loggedOut.Engine.EnsureMicrosoftCacheUpgrade();
        Check(Fixture.Dec(loggedOut.Official.Values["CacheMsV2OAuthRefresh_LF"]!) == "", "Upgrade revived explicit LF logout");
        int commands = f.Official.Commands;
        Throws<OperationCanceledException>(() => f.Engine.SaveAccountGroup(new Dictionary<string, object?>(), () => true), "Canceled account batch committed");
        Throws<OperationCanceledException>(() => f.Engine.SaveMicrosoftSession(new Dictionary<string, object?>(), "", () => true), "Canceled MS session read data");
        Check(f.Official.Commands == commands, "Cancellation touched backing store");
        f.Engine.SaveMicrosoftSession(new Dictionary<string, object?> { ["CacheMsV2Name"] = "Renamed", ["CacheMsV2OAuthRefresh"] = "new-refresh" }, "LF Name");
        Check((string)f.Engine.Get("LoginMsJson")! == "{\"Renamed\":\"new-refresh\"}", "Session/account JSON not written together");
        Check(f.Official.Writes.Last() == "CacheMsV2Migrated_LF", "Session marker not last");
    }
    private enum Login { Legacy }
    internal sealed class Instance { public string Path = "fixture"; }
    internal sealed class Entry : LegacySettingData<Instance> {
        private readonly LegacySettingSource source;
        public Action<Entry>? Persist;
        public Entry(string key, object? value, LegacySettingSource source, bool encrypted, Action<object>? changed)
            : base(key, value, encrypted, changed, (_, _) => throw new Exception("Invalid fixture definition")) => this.source = source;
        public override LegacySettingSource GetSourceCode() => source;
        protected override string GetInstancePath(Instance instance) => instance.Path;
        public override void Save(Instance? instance = null) => Persist!(this);
    }
    private sealed class Fixture {
        public const string Id = "AAAA-BBBB-CCCC-DDDD";
        public readonly Store Official = new(), Legacy = new() { IsLegacy = true };
        public readonly Dictionary<string, Entry> Entries;
        public readonly Engine Engine;
        public readonly List<string> Logs = new();
        public Fixture() {
            fixtures.Add(this);
            Entries = LegacySettingsDefinitions.Create<Entry>((k, v, s, e, c) => new Entry(k,v,s,e,c), key => key switch {
                "LoginType" => Login.Legacy, "UiLauncherHue" => 225, "UiLauncherSat" => 100, "UiLauncherDelta" => 115, "UiLauncherLight" => 40, "UiBackgroundOpacity" => 748, _ => throw new Exception("Unexpected platform default " + key)
            }, new Dictionary<string, Action<object>>());
            object gate = new();
            Engine = new(() => Entries, () => gate, () => new LegacyAccountMigrationPlan(), new LegacySettingsHost<Instance> {
                InstancePath = i => i.Path, Identify = () => Id, RegistryFolder = () => "PCLDebug",
                ReadIni = (_,_,fallback) => fallback, WriteIni = (_,_,_) => { }, DeleteIni = (_,_) => { }, HasIni = (_,_) => false,
                ReadRegistry = (_,key,fallback) => Microsoft.VisualBasic.CompilerServices.Conversions.ToString(Official.GetValue(key, fallback)),
                WriteRegistry = (_,key,value) => Official.SetValue(key,value!,RegistryValueKind.String), HasRegistry = (_,key) => Official.GetValue(key) is not null,
                OpenRegistry = (folder,_,_) => folder == "PCL" ? Official : Legacy,
                IdentifyCandidates = _ => new List<string> { Id }, Log = (_,message,_,_) => Logs.Add(message)
            });
            foreach (Entry entry in Entries.Values) entry.Persist = e => Engine.SaveEntry(e);
        }
        public static string Enc(string text) => LegacySettingsCrypto.Encrypt(text, "PCL" + Id);
        public static string Dec(object text) => LegacySettingsCrypto.Decrypt((string)text, "PCL" + Id);
    }
    private sealed class Store : ILegacySettingsStore {
        public readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal);
        public readonly Dictionary<string, RegistryValueKind> Kinds = new(StringComparer.Ordinal);
        public readonly List<string> Reads = new(), Writes = new(), ForbiddenAccesses = new();
        public bool IsLegacy;
        public string? FailWriteOnce, BadReadbackOnce, FailDeleteOnce;
        private string? pendingBadRead;
        public int Commands;
        private void Guard(string key) {
            Commands++;
            if (key == "LoginMsJson" || key.StartsWith("CacheMs", StringComparison.Ordinal) && !key.EndsWith("_LF", StringComparison.Ordinal))
            { ForbiddenAccesses.Add(key); throw new Exception((IsLegacy ? "Private" : "Shared") + " Microsoft storage accessed: " + key); }
        }
        public void Seed(string key, object? value, RegistryValueKind kind = RegistryValueKind.String) { Values[key] = value; Kinds[key] = kind; }
        public object? GetValue(string name, object? fallback = null, bool doNotExpandEnvironmentNames = false) {
            Guard(name); Reads.Add(name);
            if (pendingBadRead == name) { pendingBadRead = null; return "invalid-readback"; }
            return Values.TryGetValue(name, out object? result) ? result : fallback;
        }
        public RegistryValueKind GetValueKind(string name) { Guard(name); return Kinds[name]; }
        public void SetValue(string name, object value, RegistryValueKind kind) {
            Guard(name); Writes.Add(name);
            if (FailWriteOnce == name) { FailWriteOnce = null; throw new IOException("synthetic write failure"); }
            Values[name] = value; Kinds[name] = kind;
            if (BadReadbackOnce == name) { BadReadbackOnce = null; pendingBadRead = name; }
        }
        public void DeleteValue(string name, bool throwOnMissingValue) {
            Guard(name);
            if (FailDeleteOnce == name) { FailDeleteOnce = null; throw new IOException("synthetic cleanup failure"); }
            Values.Remove(name); Kinds.Remove(name);
        }
        public void Dispose() { }
    }
}
