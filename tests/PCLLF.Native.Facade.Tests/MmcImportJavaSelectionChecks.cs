using System.Collections.Immutable;
using System.Net.Http;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main selection and installed-Java checks; no import publication, game or external network.
internal static class MmcImportJavaSelectionChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        var profile = new JsonConfigProvider(Path.Combine(files.Root, "java-selection-profile.json"));
        ConfigProviders.Default = profile;
        Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "Import Java fixture captured a real user profile.");
        string java8 = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA8") ?? throw new IOException("Set explicit installed Java8.");
        string java21 = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA21") ?? throw new IOException("Set explicit installed Java21.");
        var catalogue = new ConcurrentList<Java>([new(Path.GetDirectoryName(java21)!), new(Path.GetDirectoryName(java8)!)]);
        Configs.JavaList.Set(catalogue); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
        string legacy = new JObject { ["Path"] = Path.Combine(files.Root, "retired-java24", "bin") }.ToString(Newtonsoft.Json.Formatting.None);
        Native.Cache("LaunchArgumentJavaSelect", legacy);
        foreach (int[] majors in new[] { new[] { 8 }, new[] { 17, 21, 25, 26 } }) {
            var metadata = new Dictionary<string, string> {
                ["mmc-pack.json"] = """{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""",
                ["patches/net.minecraft.json"] = new JObject {
                    ["formatVersion"] = 1, ["uid"] = "net.minecraft", ["version"] = "1.7.10", ["mainClass"] = "fixture.Main",
                    ["mainJar"] = new JObject { ["name"] = "fixture:game:1", ["MMC-hint"] = "local", ["MMC-filename"] = "game.jar" },
                    ["compatibleJavaMajors"] = new JArray(majors)
                }.ToString(Newtonsoft.Json.Formatting.None)
            };
            var read = MmcPackageReader.Parse(new(metadata.Keys.Select(path => new MmcArchiveEntry(path)).Append(new("minecraft/", 0, true)).ToArray(), metadata));
            Native.Check(!read.HasErrors && read.Package is not null, "Owned Java selection package did not parse.");
            var resolved = MmcComponentResolver.Resolve(read.Package!, []);
            Native.Check(resolved.IsComplete && resolved.Profile is not null, "Owned Java selection profile did not resolve.");
            var result = ((Task<MmcRuntimeEnvironment>)Native.Static("PCL.MmcInstances", "SelectJavaAsync", read.Package!, resolved.Profile!, CancellationToken.None)!).GetAwaiter().GetResult();
            int expected = majors[0] == 8 ? 8 : 21;
            Native.Check(result.JavaMajor == expected && result.Architecture == "x86_64", "Retired global Java metadata overrode current compatible candidates.");
        }
        Native.Check(Settings.Get<string>("LaunchArgumentJavaSelect") == legacy && Configs.JavaList.Get()!.SequenceEqual(catalogue),
            "Import Java selection changed the user's retained setting or catalogue.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS MMC import selects actual installed Java8/21 from the current catalogue despite a retired Java24 selection; no user settings change, game or network.");
    }
    internal static void RunGtnh(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        var profile = new JsonConfigProvider(Path.Combine(files.Root, "java-selection-profile.json")); ConfigProviders.Default = profile;
        Native.Check(ReferenceEquals(Configs.JavaList.Provider, profile), "GTNH Java probe captured a real user profile.");
        string java8 = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA8") ?? throw new IOException("Set installed Java8.");
        string java21 = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_JAVA21") ?? throw new IOException("Set installed Java21.");
        Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java21)!), new(Path.GetDirectoryName(java8)!)]));
        Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
        Native.Cache("LaunchArgumentJavaSelect", new JObject { ["Path"] = Path.Combine(files.Root, "retired-java24", "bin") }.ToString(Newtonsoft.Json.Formatting.None));
        string cache = Environment.GetEnvironmentVariable("PCLLF_MMC_TEST_GTNH_METADATA") ?? throw new IOException("Set the fixed existing GTNH metadata cache.");
        using HttpClient client = new(new NoNetwork());
        var store = new MmcMetadataStore(cache, new Uri("https://meta.prismlauncher.org/v1/"), client);
        foreach (var sample in new[] {
            (Key: "PCLLF_MMC_TEST_GTNH8", Major: 8, Sha: "ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af"),
            (Key: "PCLLF_MMC_TEST_GTNH_MODERN", Major: 21, Sha: "6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16")
        }) {
            string archive = Environment.GetEnvironmentVariable(sample.Key) ?? throw new IOException("Set explicit original GTNH ZIP: " + sample.Key);
            using var source = MmcArchiveSourceReader.ReadAsync(archive).GetAwaiter().GetResult();
            Native.Check(source.PackageSha256 == sample.Sha, "GTNH original differs from the fixed supplied package.");
            var resolved = MmcMetadataResolution.ResolveAsync(source.Package, store).GetAwaiter().GetResult();
            Native.Check(resolved.Resolution.IsComplete && resolved.Resolution.Profile is not null, "Fixed GTNH metadata did not resolve offline.");
            var selected = ((Task<MmcRuntimeEnvironment>)Native.Static("PCL.MmcInstances", "SelectJavaAsync", source.Package, resolved.Resolution.Profile!, CancellationToken.None)!).GetAwaiter().GetResult();
            Native.Check(selected.JavaMajor == sample.Major, "Actual GTNH package selected an incompatible/retired Java.");
            Console.WriteLine("PASS original " + Path.GetFileName(archive) + ": actual Main selects Java " + selected.JavaMajor + "; exact ZIP SHA256, fixed metadata, no publication/game/network.");
        }
        environment.AssertNoForbiddenIo();
    }
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new IOException("GTNH Java selection proof must use already fixed metadata, never download.");
    }
}
