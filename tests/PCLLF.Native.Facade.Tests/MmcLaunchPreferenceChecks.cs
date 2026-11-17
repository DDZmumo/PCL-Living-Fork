using System.Collections.Immutable;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcLaunchPreferenceChecks {
    internal static void Run(FixtureEnvironment environment) {
        string root = Path.Combine(environment.Data, "mmc-launch-preferences"), archive = Path.Combine(root, "fixture.zip"), version = Path.Combine(root, "game", "versions", "preference-fixture");
        Directory.CreateDirectory(root); ConfigProviders.Default = new JsonConfigProvider(Path.Combine(root, "private-config.json"));
        Settings.Entries["LaunchAdvanceJvm"].ValueCache[""] = "-Dglobal=present";
        Settings.Entries["LaunchRamType"].ValueCache[""] = 1; Settings.Entries["LaunchRamCustom"].ValueCache[""] = 15;
        byte[] index = Encoding.UTF8.GetBytes("{\"objects\":{}}"); string sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(index));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
            Add("pack/mmc-pack.json", """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
            Add("pack/instance.cfg", "name=Preferences\nOverrideJavaLocation=1\nJavaPath=C:/recommended/bin/java.exe\nOverrideJavaArgs=1\nJvmArgs=-Dhello=\"a b\" \"\" -Dduplicate=one -Dduplicate=two\nOverrideMemory=1\nMinMemAlloc=512\nMaxMemAlloc=4096\nOverrideWindow=1\nMinecraftWinWidth=1280\nMinecraftWinHeight=720\nLaunchMaximized=1\n");
            Add("pack/patches/net.minecraft.json", $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"+jvmArgs":["-Drequired=kept"],"assetIndex":{"id":"fixture","url":"https://fixture.invalid/assets.json","sha1":"{{sha1}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
            Add("pack/libraries/fixture.jar", "owned local bytes");
            void Add(string path, string text) { using var output = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); output.Write(text); }
        }
        using HttpClient client = new(new NoNetwork());
        var metadata = new MmcMetadataStore(Path.Combine(root, "metadata"), new Uri("https://metadata.invalid/"), client);
        var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(metadata, new(Path.Combine(root, "cache", "libraries"), Path.Combine(root, "cache", "assets"), Path.Combine(root, "cache", "source")), value => value,
            async (files, token) => { foreach (var file in files) await File.WriteAllBytesAsync(file.DownloadPath, index, token); });
        var imported = backend.ImportAsync(new(archive, version, "preference-fixture", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
        Native.Check(imported.Status == MmcInstanceBackendStatus.Succeeded, "Preference fixture failed to publish: " + imported.Failure);
        var instance = new ModMinecraft.McInstance(version); var cfg = imported.PackageConfiguration!; Guid session = Guid.NewGuid();
        var package = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(package.RuntimeSettings.UserJvmTokens.SequenceEqual(new[] { "-Dhello=a b", "", "-Dduplicate=one", "-Dduplicate=two" }), "Package JVM tokens were joined, deduplicated or lost an explicit empty token.");
        Native.Check(imported.BoundPlan!.CompiledProfile.JvmArguments.Any(value => value.Value == "-Drequired=kept"), "Required component JVM args are missing from the separate Core plan.");
        Native.Check(package.JavaPreference.Mode == MmcLaunchPreferenceMode.UsePackage && !package.JavaPreference.Force && package.JavaPreference.Path == cfg.Values["JavaPath"], "Package JavaPath became a forced Java or changed after Core decoding.");
        Native.Check(package.RuntimeSettings.MinimumMemoryMiB == 512 && package.RuntimeSettings.MaximumMemoryMiB == 4096 && package.RuntimeSettings.ResolutionWidth == 1280 && package.RuntimeSettings.ResolutionHeight == 720 && package.MaximizeWindow && !package.RuntimeSettings.Fullscreen, "Package memory/window semantics changed.");
        Settings.Set("VersionAdvanceJvm", "-Dbackground=ignored", instance);
        Native.Check(MmcLaunchPreferences.Capture(instance, cfg, session).RuntimeSettings.UserJvmTokens.SequenceEqual(package.RuntimeSettings.UserJvmTokens), "Non-user Settings.Set changed a package mode.");
        using (MmcConfigurationCoordinator.Quiesce(version)) {
            using var lease = MmcAdmissionGate.Acquire(version, imported.Definition!.InstanceId, TimeSpan.Zero);
            Task userWrite = Task.Run(() => Settings.SetInstanceUserPreference("VersionAdvanceJvm", "", instance));
            Native.Check(userWrite.Wait(1000), "User preference write waited for a foreign admission owner."); userWrite.GetAwaiter().GetResult();
            var overlay = MmcIniConfiguration.CapturePreferences(instance.Config);
            Native.Check(overlay.Ini["MmcJvmArgsMode"] == "Custom" && overlay.Ini["VersionAdvanceJvm"] == "", "Value and mode were not visible as one queued patch.");
            var empty = MmcLaunchPreferences.Capture(instance, cfg, session);
            Native.Check(empty.RuntimeSettings.UserJvmTokens.IsEmpty, "Explicit empty custom JVM args fell back to global/package args.");
            MmcLaunchPreferences.Revalidate(instance, empty);
        }
        Settings.ResetInstanceUserPreference("VersionAdvanceJvm", instance);
        Native.Check(MmcLaunchPreferences.Capture(instance, cfg, session).RuntimeSettings.UserJvmTokens.SequenceEqual(new[] { "-Dglobal=present" }), "Explicit reset did not inherit globals.");
        MmcLaunchPreferences.SetForcedJava(instance, new Java(Path.Combine(root, "chosen-java", "bin")));
        var forced = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(forced.JavaPreference.Mode == MmcLaunchPreferenceMode.Custom && forced.JavaPreference.Force && forced.JavaPreference.Path!.Contains("chosen-java"), "Actual JavaForced JSON was not captured with the mode.");
        Settings.Set("VersionArgumentJavaSelect", new JObject { ["Path"] = Path.Combine(root, "old-legacy-java") }.ToString(Newtonsoft.Json.Formatting.None), instance);
        Settings.SetInstanceUserPreference("VersionArgumentJavaV2", 0, instance);
        var automatic = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(automatic.JavaPreference.Path is null && !automatic.JavaPreference.Force, "An explicit automatic Java selection reused obsolete forced Java metadata.");
        string retiredGlobal = new JObject { ["Path"] = Path.Combine(root, "retired-global-java24") }.ToString(Newtonsoft.Json.Formatting.None);
        Native.Cache("LaunchArgumentJavaSelect", retiredGlobal);
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string?> { ["MmcJavaLocationMode"] = "InheritGlobal" });
        var inheritedJava = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(inheritedJava.JavaPreference.Mode == MmcLaunchPreferenceMode.InheritGlobal && !inheritedJava.JavaPreference.Force && inheritedJava.JavaPreference.Path is null &&
            Settings.Get<string>("LaunchArgumentJavaSelect") == retiredGlobal, "Current global Java catalogue order was replaced by a retired forced path, or the user's old setting was overwritten.");
        Settings.SetInstanceUserPreference("VersionRamType", 1, instance); Settings.SetInstanceUserPreference("VersionRamCustom", 20, instance);
        Native.Check(MmcLaunchPreferences.Capture(instance, cfg, session).RuntimeSettings.MaximumMemoryMiB == 5632, "User memory override did not replace package memory.");
        MmcLaunchPreferences.SetWindowOverride(instance, 1000, 600, fullscreen: true);
        var current = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(current.RuntimeSettings.ResolutionWidth == 1000 && current.RuntimeSettings.Fullscreen && !current.MaximizeWindow, "Custom window mode did not override package window.");
        bool rejected = false; try { MmcLaunchPreferences.Revalidate(instance, forced); } catch (IOException) { rejected = true; }
        Native.Check(rejected, "Changed launch settings were not rejected before process creation.");
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string?> { ["MmcMemoryMode"] = "InheritGlobal", ["MmcWindowMode"] = "InheritGlobal" });
        var inherited = MmcLaunchPreferences.Capture(instance, cfg, session);
        Native.Check(inherited.RuntimeSettings.MaximumMemoryMiB == 3072 && inherited.RuntimeSettings.ResolutionWidth == 854 && inherited.RuntimeSettings.ResolutionHeight == 480, "Global memory/window preferences were not inherited independently.");
        MmcLaunchPreferences.SetWindowOverride(instance, 1000, 600, fullscreen: true);
        MmcIniConfiguration.QueuePreferenceBatch(instance.Config, new Dictionary<string, string?> { ["MmcMemoryMode"] = "UsePackage" });
        var invalid = cfg with { Values = cfg.Values.SetItem("MaxMemAlloc", "") };
        rejected = false; try { MmcLaunchPreferences.Capture(instance, invalid, session); } catch (InvalidDataException) { rejected = true; }
        Native.Check(rejected, "Empty numeric override silently inherited defaults.");
        instance.Config.Save();
        using (var reopened = new InstanceConfigProvider(version)) {
            var saved = MmcIniConfiguration.CapturePreferences(reopened);
            Native.Check(saved.Ini["MmcJvmArgsMode"] == "InheritGlobal" && saved.Ini["MmcWindowMode"] == "Custom" && saved.Config["InstanceForcedJava"] is JObject, "Queued preferences did not survive reopen coherently.");
        }
        string ordinaryRoot = Path.Combine(root, "ordinary-temp"); Directory.CreateDirectory(ordinaryRoot); var ordinary = new ModMinecraft.McInstance(ordinaryRoot);
        Settings.SetInstanceUserPreference("VersionAdvanceJvm", "-Dordinary=preserved", ordinary); ordinary.Config.Save();
        Native.Check(Settings.Get<string>("VersionAdvanceJvm", ordinary) == "-Dordinary=preserved" && !File.ReadAllText(Path.Combine(ordinaryRoot, "PCL", "Setup.ini")).Contains("MmcJvmArgsMode"), "Ordinary instance settings gained MMC mode behavior.");
        InstanceConfigProviders.FlushAndDisposeWithin(root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS MMC preferences: package/global/custom, empty JVM override, required argument separation, atomic queued overlays, JavaForced, memory/window, final fingerprint and ordinary fallback; no game launch.");
    }
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new IOException("No network is permitted in the preference fixture.");
    }
}
