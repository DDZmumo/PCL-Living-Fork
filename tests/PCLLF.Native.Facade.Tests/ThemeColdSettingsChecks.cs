using System.Collections;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class ThemeColdSettingsChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        LegacyHiddenPreferences();
        IDictionary cache = Settings.Entries["UiLauncherColorMode"].ValueCache;
        var saved = new List<DictionaryEntry>();
        foreach (DictionaryEntry entry in cache) saved.Add(entry);
        int suppression = ModAnimation.AniControlEnabled;
        try
        {
            foreach (int mode in new[] { 0, 1 })
            {
                Native.Cache("UiLauncherColorMode", mode);
                var page = new PageSetupUI();
                var choice = (ComboBox)page.FindName("ComboLauncherColorMode");
                Native.Check(!page.IsLoaded && choice.SelectedIndex == mode, "Cold setup BAML does not select the saved launcher color mode before Loaded.");
                Native.Check(ModAnimation.AniControlEnabled == suppression && Settings.Get<int>("UiLauncherColorMode") == mode,
                    "Cold setup theme selection leaked animation suppression or changed the saved preference.");
            }
            environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS cold setup theme: real BAML selects both saved modes before Loaded; preference and animation suppression preserved, no forbidden I/O.");
        }
        finally
        {
            cache.Clear();
            foreach (DictionaryEntry entry in saved) cache.Add(entry.Key, entry.Value);
            ModAnimation.AniControlEnabled = suppression;
        }
    }

    private static void LegacyHiddenPreferences()
    {
        string[] hidden = Settings.Entries.Keys.Where(key => key.StartsWith("UiHidden", StringComparison.Ordinal)).ToArray();
        string[] keys = hidden.Concat(new[] { "UiShellStyle", "UiLauncherEmail" }).ToArray();
        var saved = new Dictionary<string, List<DictionaryEntry>>();
        foreach (string key in keys) {
            saved[key] = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in (IDictionary)Settings.Entries[key].ValueCache) saved[key].Add(entry);
        }
        try {
            Native.Check(hidden.Length > 0 && hidden.All(key => Settings.Entries[key].DefaultValue is bool), "Legacy visibility preferences no longer have the expected boolean contract.");
            foreach (string key in hidden) Native.Cache(key, true);
            Native.Cache("UiLauncherEmail", true);
            foreach (int mode in new[] { 0, 1, 0 }) {
                Native.Cache("UiShellStyle", mode);
                bool expected = mode == 0;
                Native.Check(hidden.All(key => Settings.Get<bool>(key) == expected && Equals(Settings.Get(key), expected) && Equals(Settings.GetSafe(key), expected)),
                    "Visibility reads did not follow the selected classic/modern shell through every public settings read path.");
                Native.Check(hidden.All(key => ((IDictionary)Settings.Entries[key].ValueCache)[""] is true) &&
                    Settings.Get<bool>("UiLauncherEmail") && Equals(Settings.GetSafe("UiLauncherEmail"), true),
                    "Modern visibility policy changed a stored hidden preference or suppressed the unrelated email/privacy preference.");
            }
            Native.Cache("UiShellStyle", 1);
            IDictionary cold = Settings.Entries[hidden[0]].ValueCache; cold.Remove("");
            Native.Check(Equals(Settings.Get(hidden[0]), false) && Equals(Settings.GetSafe(hidden[0]), false) && !cold.Contains(""),
                "A modern hidden-setting read entered the backend and populated its original cold cache.");
        }
        finally {
            foreach (string key in keys) {
                IDictionary cache = Settings.Entries[key].ValueCache; cache.Clear();
                foreach (DictionaryEntry entry in saved[key]) cache.Add(entry.Key, entry.Value);
            }
        }
        Console.WriteLine("PASS shell visibility preferences: classic/modern/classic reads preserve every cached UiHidden value and unrelated privacy preference.");
    }
}
