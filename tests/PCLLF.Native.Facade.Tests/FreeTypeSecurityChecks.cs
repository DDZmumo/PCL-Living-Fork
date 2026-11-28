// SPDX-License-Identifier: MPL-2.0
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class FreeTypeSecurityChecks
{
    private const string Prefix = "org.lwjgl:lwjgl-freetype:";
    private const string Patched341 = "3.4.1-mojang-2.15.0.3";
    private const string Patched343 = "3.4.3-mojang-2.15.0";
    private const string Windows = ":natives-windows";

    internal static void Run(FixtureEnvironment environment)
    {
        using var scope = new FileTaskScope(environment);
        string game = Path.Combine(scope.Root, "freetype-game");
        scope.SelectMinecraft(game);
        ModMinecraft.McInstance latest = Create(game, "26.3", "3.4.3");
        ModMinecraft.McInstance earlier = Create(game, "26.2", "3.4.1");
        ModMinecraft.McInstance untouched = Create(game, "1.21.11", "3.3.3");
        string jsonBefore = File.ReadAllText(latest.GetJsonPath());
        JToken resolvedBefore = latest.JsonObject.DeepClone();

        var x64 = FontLibraries(ModMinecraft.McLibListGet(latest, false));
        Native.Check(x64.Select(library => library.OriginalName).SequenceEqual(new[] {
            Prefix + Patched343, Prefix + Patched343 + Windows, Prefix + Patched343 + Windows + "-arm64"
        }), "The native launch resolver did not replace the old FreeType pair with the verified 26.3 family.");
        var other = ModMinecraft.McLibListGet(latest, false).Single(library => library.OriginalName == "org.lwjgl:lwjgl:3.4.3");
        Native.Check(other.Url == "https://fixture.invalid/lwjgl.jar", "The security update changed an unrelated LWJGL library.");
        Native.Check(x64.All(library => Uri.TryCreate(library.Url, UriKind.Absolute, out var address) && address.Host == "libraries.minecraft.net" &&
            library.LocalPath.StartsWith(game, StringComparison.OrdinalIgnoreCase)), "A security artifact did not use a verified source and the fixture library root.");

        var x86 = FontLibraries(ModMinecraft.McLibListGetForX86(earlier, false));
        Native.Check(x86.Select(library => library.OriginalName).SequenceEqual(new[] { Prefix + Patched341, Prefix + Patched341 + Windows + "-x86" }),
            "32-bit Java selected the wrong FreeType classifier.");
        try {
            ModMinecraft.McLibListGetForX86(latest, false);
            throw new InvalidOperationException("The launcher admitted an architecture with no verified FreeType artifact.");
        } catch (InvalidOperationException error) when (error.Message.Contains("32 位 Windows", StringComparison.Ordinal)) { }
        Native.Check(FontLibraries(ModMinecraft.McLibListGet(untouched, false)).All(library => library.OriginalName!.StartsWith(Prefix + "3.3.3", StringComparison.Ordinal)),
            "The targeted update changed a 1.21.11 instance.");

        // The same fixture is normally ordinary. Its managed presentation flag
        // also proves the ordinary library resolver excludes the MMC route.
        var managed = Native.Property(typeof(ModMinecraft.McInstance).BaseType!, "UsesMmcBackend");
        managed.SetValue(latest, true);
        try {
            Native.Check(FontLibraries(ModMinecraft.McLibListGet(latest, false)).All(library => library.OriginalName!.StartsWith(Prefix + "3.4.3:", StringComparison.Ordinal) || library.OriginalName == Prefix + "3.4.3"),
                "The ordinary security overlay changed the MMC library result.");
        } finally { managed.SetValue(latest, false); }

        Settings.Entries["VersionAdvanceAssetsV2"].ValueCache[latest.PathVersion] = true;
        foreach (var library in x64) {
            Directory.CreateDirectory(Path.GetDirectoryName(library.LocalPath)!);
            File.WriteAllText(library.LocalPath, "corrupted fixture library");
        }
        var files = ModMinecraft.McLibNetFilesFromInstance(latest);
        foreach (var library in x64) {
            var download = files.Single(file => string.Equals(Native.Full(file.LocalPath), Native.Full(library.LocalPath), StringComparison.OrdinalIgnoreCase));
            Native.Check(download.Check.Hash?.Length == 64 && download.Check.ActualSize == library.Size && download.Check.Check(download.LocalPath) is not null,
                "Ignore file checks suppressed integrity verification of a security artifact.");
        }
        Type gate = typeof(LegacyMinecraftLibrary).Assembly.GetType("PCLCS.LegacyFreeTypeSecurityPatch", throwOnError: true)!;
        try {
            Native.Call(gate, "VerifyBeforeLaunch", null, x64.Cast<LegacyMinecraftLibrary>().ToArray(), Path.Combine(game, "natives"));
            throw new InvalidOperationException("The ordinary launch gate accepted a failed security check.");
        } catch (IOException error) when (error.Message.Contains("FreeType 安全库校验失败", StringComparison.Ordinal)) { }
        Native.Check(File.ReadAllText(latest.GetJsonPath()) == jsonBefore,
            "FreeType replacement rewrote the installed version manifest.");
        Native.Check(JToken.DeepEquals(resolvedBefore, latest.JsonObject), "FreeType replacement changed the cached version manifest.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS FreeType security: ordinary 26.x resolution, Windows classifiers, unaffected MMC and 1.21.11, mandatory SHA-256 and unchanged saved manifests.");
    }

    private static List<ModMinecraft.McLibEntry> FontLibraries(IEnumerable<ModMinecraft.McLibEntry> libraries) =>
        libraries.Where(library => library.OriginalName?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToList();

    private static ModMinecraft.McInstance Create(string game, string version, string lwjgl)
    {
        string folder = Directory.CreateDirectory(Path.Combine(game, "versions", version)).FullName;
        var metadata = new JObject {
            ["id"] = version, ["type"] = "release", ["mainClass"] = "fixture.Main", ["releaseTime"] = "2026-09-15T11:23:02Z",
            ["libraries"] = new JArray(
                new JObject { ["name"] = Prefix + lwjgl, ["url"] = "https://fixture.invalid/" },
                new JObject { ["name"] = Prefix + lwjgl + Windows },
                new JObject { ["name"] = Prefix + lwjgl + Windows + "-x86" },
                new JObject { ["name"] = "org.lwjgl:lwjgl:3.4.3", ["downloads"] = new JObject { ["artifact"] = new JObject {
                    ["url"] = "https://fixture.invalid/lwjgl.jar", ["size"] = 128
                } } })
        };
        File.WriteAllText(Path.Combine(folder, version + ".json"), metadata.ToString());
        var instance = new ModMinecraft.McInstance(folder) { IsLoaded = true, Version = new McVersion(version, true) };
        FileTaskScope.CacheInstance(instance);
        return instance;
    }
}
