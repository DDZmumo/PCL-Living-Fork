// SPDX-License-Identifier: MPL-2.0
using System.Windows.Controls;
using MeloongCore;
using Microsoft.Win32;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class LaunchOptionsResetChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Data, "launch-settings-reset");
        string versionRoot = Path.Combine(root, "versions", "ordinary");
        Directory.CreateDirectory(versionRoot);
        var previousProfile = ConfigProviders.Default;
        var profile = new JsonConfigProvider(Path.Combine(root, "java-profile.json"));
        ConfigProviders.Default = profile;
        try
        {
            Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile) && ReferenceEquals(Configs.JavaRemovedList.Provider, profile),
                "The Java catalogue reset check requires its own configuration and must run alone.");
            ResetLaunchOptions(root, versionRoot);
            environment.AssertNoForbiddenIo();
        }
        finally
        {
            try { profile.Save(); }
            finally { ConfigProviders.Default = previousProfile; }
        }
    }

    private static void ResetLaunchOptions(string root, string versionRoot)
    {
        var first = new Java(Path.Combine(root, "java-17", "bin"), new Version(17, 0));
        var chosen = new Java(Path.Combine(root, "java-21", "bin"), new Version(21, 0));
        string removed = Path.Combine(root, "ignored-java", "bin");
        Configs.JavaList.Set(new ConcurrentList<Java>([first, chosen]));
        Configs.JavaRemovedList.Set([removed]);

        var instance = new ModMinecraft.McInstance(versionRoot) { IsLoaded = true, Version = new McVersion("1.21.1") };
        const string range = "[17, 22)";
        Settings.Set("LaunchArgumentTitle", "Global fixture title");
        Settings.Set("LaunchArgumentIndieV2", 0);
        Settings.Set("VersionArgumentTitle", "Instance fixture title", instance);
        Settings.Set("VersionArgumentIndieV2", true, instance);
        Settings.Set("VersionAdvanceAssets", 2, instance);
        Settings.Set("VersionArgumentJavaV2", 3, instance);
        Settings.Set("VersionArgumentJavaRange", range, instance);
        MmcLaunchPreferences.SetForcedJava(instance, chosen);

        var previousInstance = LauncherInstanceContext.Instance;
        var previousWorker = ModJava.JavaListRefreshWorker;
        var refresh = new RedoableWorker(() => { });
        ModJava.JavaListRefreshWorker = refresh;
        try
        {
            var main = FileTaskShell.Install();
            LauncherInstanceContext.Instance = instance;
            var instancePage = (GlassLaunchOptionsPage)main.GlassInstanceNavigation.GetPage(FormMain.PageSubType.InstanceSetup);
            instancePage.Reset();
            Native.Check(Settings.Get<string>("VersionArgumentTitle", instance) == "" && !Settings.HasSaved("VersionArgumentTitle", instance) &&
                !Settings.Get<bool>("VersionArgumentIndieV2", instance) && !Settings.HasSaved("VersionAdvanceAssets", instance) &&
                !Settings.Get<bool>("VersionAdvanceAssetsV2", instance), "Resetting the instance page kept its launch options.");
            CheckJavaBinding("instance");

            ResetGlobalLaunchOptions(main);
            Native.Check(Settings.Get<string>("LaunchArgumentTitle") == "" && !Settings.HasSaved("LaunchArgumentTitle"),
                "Resetting the global page kept its launch options.");
            Native.Check(Configs.JavaList.Get()!.SequenceEqual([first, chosen]) && Configs.JavaRemovedList.Get()!.SequenceEqual([removed]),
                "Resetting launch settings changed the global Java catalogue, its priority, or its exclusions.");
            CheckJavaBinding("global");
            Native.Check(refresh.WaitIfRunning(5000) && !refresh.HasSucceeded, "Resetting launch settings started Java discovery.");

            var javaPage = new GlassInstanceJavaPage(() => { });
            try
            {
                javaPage.Open(loadData: true);
                var picker = (ComboBox)Native.Get(javaPage, "_picker")!;
                Native.Check(Equals(picker.SelectedItem, chosen), "The independent Java page lost its displayed selection after reset.");
            }
            finally { javaPage.Leave(); }
        }
        finally
        {
            ModJava.JavaListRefreshWorker = previousWorker;
            LauncherInstanceContext.Instance = previousInstance;
            FileTaskShell.CloseUnshownHosts();
        }

        void CheckJavaBinding(string scope) => Native.Check(Settings.Get<int>("VersionArgumentJavaV2", instance) == 3 &&
            Settings.Get<string>("VersionArgumentJavaRange", instance) == range && Equals(Configs.JavaForced.Get(instance.Config), chosen),
            $"Resetting the {scope} page changed the instance's Java mode, range, or binding.");
    }

    private static void ResetGlobalLaunchOptions(FormMain main)
    {
        var backend = Native.Field(typeof(Settings), "Backend").GetValue(null)!;
        var host = (LegacySettingsHost<ModMinecraft.McInstance>)Native.Get(backend, "host")!;
        var previous = host.OpenRegistry;
        var registry = new LaunchRegistryStore();
        host.OpenRegistry = (folder, write, create) => folder == "PCL" && write && create
            ? registry : previous(folder, write, create);
        try { main.ResetGlassLaunchOptions(); }
        finally { host.OpenRegistry = previous; }
        registry.CheckReset();
    }

    private sealed class LaunchRegistryStore : ILegacySettingsStore
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal) {
            ["LaunchArgumentVisible"] = null, ["LaunchArgumentPriority"] = null, ["LaunchArgumentRam"] = null,
            ["LaunchAdvanceGraphicCard"] = null, ["ToolDownloadCert"] = null
        };
        private bool _unexpectedAccess;

        public object? GetValue(string name, object? defaultValue = null, bool doNotExpandEnvironmentNames = false) =>
            _values.TryGetValue(name, out var value) ? value ?? defaultValue : throw UnexpectedAccess();

        public void SetValue(string name, object value, RegistryValueKind kind)
        {
            if (!_values.ContainsKey(name) || kind != RegistryValueKind.String || value is not string text) throw UnexpectedAccess();
            _values[name] = text;
        }

        public RegistryValueKind GetValueKind(string name) => throw UnexpectedAccess();
        public void DeleteValue(string name, bool throwOnMissingValue) => throw UnexpectedAccess();
        public void Dispose() { }

        private Exception UnexpectedAccess()
        {
            _unexpectedAccess = true;
            return new InvalidOperationException("Unexpected Registry entry in the launch options reset.");
        }

        internal void CheckReset() => Native.Check(!_unexpectedAccess && _values.All(pair => pair.Value == Settings.GetDefault(pair.Key)),
            "Resetting the global page did not restore its Registry-backed launch options.");
    }
}
