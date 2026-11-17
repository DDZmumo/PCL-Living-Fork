using System.Collections.Concurrent;
using System.Diagnostics;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS;

// LF ModJava.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 is the behavior source,
// identical here to PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8. CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 was reference only: no new Java rules.
internal static class LegacyJavaTests {
    private const string ChildFlag = "--internal-java-compatibility";
    private const string Passed = "Actual LF Java compatibility passed: ";
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("java requirements preserve LF loader intersections and conflict replacement", () => RunChild("requirements"));
        yield return ("java metadata forced ranges and account bounds use actual Minecraft versions", () => RunChild("metadata"));
        yield return ("java old settings migration restores UI on success malformed JSON and cancellation", () => RunChild("migration"));
        yield return ("java forced and automatic selection preserve checked cache order without fallback", () => RunChild("selection"));
        yield return ("java refresh uses the real worker wait cancellation and manual invalidation boundaries", () => RunChild("worker"));
        yield return ("java Mojang parsing download plans and canceled waits retain cleanup semantics", () => RunChild("download"));
        yield return ("java cache reads historical Version objects without changing current JSON or failure recovery", () => RunChild("cache-format"));
        yield return ("managed java retires only the proven Minecraft or Forge inference", () => RunChild("managed-retirement"));
        yield return ("managed java retains account user and unrelated loader bounds", () => RunChild("managed-hard-bounds"));
        yield return ("managed java keeps conflicting bounds empty while ordinary fallback stays unchanged", () => RunChild("managed-conflicts"));
    }

    // Program forwards only this exact protocol before any group touches Configs.
    internal static bool TryRunChild(string[] args, out int exitCode) {
        exitCode = 0;
        if (args.Length == 0 || args[0] != ChildFlag) return false;
        try {
            if (args.Length != 2) throw new ArgumentException("Expected the exact Java child flag and one case key.");
            using (Fixture fixture = new()) {
                switch (args[1]) {
                    case "requirements": Requirements(fixture); break;
                    case "metadata": Metadata(fixture); break;
                    case "migration": Migration(fixture); break;
                    case "selection": Selection(fixture); break;
                    case "worker": Worker(fixture); break;
                    case "download": Download(fixture); break;
                    case "cache-format": CacheFormat(fixture); break;
                    case "managed-retirement": ManagedRetirement(fixture); break;
                    case "managed-hard-bounds": ManagedHardBounds(fixture); break;
                    case "managed-conflicts": ManagedConflicts(fixture); break;
                    default: throw new ArgumentException("Unknown Java child case: " + args[1]);
                }
            }
            Console.WriteLine(Passed + args[1]);
        } catch (Exception error) {
            Console.Error.WriteLine(error.ToString());
            exitCode = 1;
        }
        return true;
    }

    private static void RunChild(string key) => RunChildAsync(key).GetAwaiter().GetResult();
    private static async Task RunChildAsync(string key) {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the actual migration test executable.");
        ProcessStartInfo start = new(executable) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(LegacyJavaTests).Assembly.Location);
        start.ArgumentList.Add(ChildFlag); start.ArgumentList.Add(key);
        using Process child = new() { StartInfo = start };
        Require(child.Start(), "The Java compatibility child did not start.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        try {
            try {
                await Task.WhenAll(child.WaitForExitAsync(), output, errors).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            } catch (TimeoutException) {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                throw new TimeoutException("Java child exceeded 20 seconds: " + key + "\n" +
                    (output.IsCompletedSuccessfully ? output.Result : "stdout incomplete") + "\n" +
                    (errors.IsCompletedSuccessfully ? errors.Result : "stderr incomplete"));
            }
            string stdout = await output.ConfigureAwait(false), stderr = await errors.ConfigureAwait(false);
            Require(child.ExitCode == 0 && stdout.Contains(Passed + key, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(stderr),
                "Java child failed (" + key + ", exit " + child.ExitCode + ").\n" + stdout + "\n" + stderr);
        } finally {
            if (!child.HasExited) {
                child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(3000), "Java child remained alive after termination.");
            }
        }
    }

    private static void CacheFormat(Fixture f) {
        JObject LegacyVersion(int major, int minor, int build, int revision) => new() {
            ["Major"] = major, ["Minor"] = minor, ["Build"] = build, ["Revision"] = revision,
            ["MajorRevision"] = revision >> 16, ["MinorRevision"] = unchecked((short)revision), ["future"] = new JObject { ["keep"] = "version-data" }
        };
        JObject RootFor(JToken? version, bool omit = false) {
            JObject java = new() { ["Folder"] = Path.Combine(f.Root, "cached-java"), ["futureJava"] = new JObject { ["keep"] = "entry-data" } };
            if (!omit) java["Version"] = version?.DeepClone() ?? JValue.CreateNull();
            return new JObject { ["JavaConfigVersion"] = Versions.JavaConfigVersion, ["JavaList"] = new JArray(java), ["unrelated"] = new JObject { ["keep"] = "unknown-root" } };
        }
        void Load(JObject json) {
            f.Provider.Save(); File.WriteAllText(f.GlobalConfigPath, json.ToString(Newtonsoft.Json.Formatting.Indented)); f.Provider.DiscardCache();
        }
        void Read(JToken? version, Version? expected, bool omit = false) {
            JObject json = RootFor(version, omit); Load(json); byte[] before = File.ReadAllBytes(f.GlobalConfigPath); int starts = f.Starts;
            var list = Configs.JavaList.Get()!;
            Require(list.Count == 1 && Equals(list[0].Version, expected), "Actual Java cache lost historical/string/null Version data.");
            f.Engine.JavaInit(); f.Provider.Save();
            Require(f.Starts == starts && File.ReadAllBytes(f.GlobalConfigPath).SequenceEqual(before), "Reading a valid Java cache rewrote bytes/unknown data or triggered a search.");
            Require(JToken.DeepEquals(JObject.Parse(File.ReadAllText(f.GlobalConfigPath)), json), "Valid Java cache read changed unknown root/entry/version members.");
        }
        Read(LegacyVersion(17, 0, 20, 1), new Version(17, 0, 20, 1));
        Read(LegacyVersion(8, 0, -1, -1), new Version(8, 0));
        Read(LegacyVersion(21, 0, 9, -1), new Version(21, 0, 9));
        Read(new JValue("21.0.9.0"), new Version(21, 0, 9, 0));
        Read(new JValue("17.0"), new Version(17, 0)); Read(new JValue(""), null); Read(null, null); Read(null, null, true);
        // Writes deliberately retain the current .NET serializer representation;
        // the compatibility reader itself never normalizes the original file.
        var current = Configs.JavaList.Get()!; current[0].Version = new Version(17, 0, 20, 1); Configs.JavaList.Set(current); f.Provider.Save();
        JObject written = JObject.Parse(File.ReadAllText(f.GlobalConfigPath));
        Require((string?)written["JavaList"]![0]!["Version"] == "17.0.20.1" && (string?)written["unrelated"]!["keep"] == "unknown-root", "Current Java write contract or unrelated data changed.");
        List<JToken> bad = [new JValue("not-a-version"), new JValue(17), new JValue(true), new JArray(17, 0), new JObject(), LegacyVersion(-1, 0, 0, 0), LegacyVersion(17, -1, 0, 0), LegacyVersion(17, 0, -2, 0), LegacyVersion(17, 0, -1, 0)];
        JObject missing = LegacyVersion(17, 0, 0, 0); missing.Remove("Minor"); bad.Add(missing);
        JObject fraction = LegacyVersion(17, 0, 0, 0); fraction["Build"] = 0.5; bad.Add(fraction);
        JObject overflow = LegacyVersion(17, 0, 0, 0); overflow["Major"] = (long)int.MaxValue + 1; bad.Add(overflow);
        foreach (JToken token in bad) {
            Load(RootFor(token)); bool rejected = false;
            try { _ = Configs.JavaList.Get(); } catch (Newtonsoft.Json.JsonSerializationException) { rejected = true; }
            Require(rejected, "Malformed Java cache unexpectedly became a usable version: " + token.Type);
            f.Engine.JavaInit(); f.Provider.Save();
            Require(!Configs.JavaList.HasValue() && (string?)JObject.Parse(File.ReadAllText(f.GlobalConfigPath))["unrelated"]!["keep"] == "unknown-root", "Original JavaInit malformed-cache reset boundary or unrelated data changed.");
        }
        Console.WriteLine("PASS actual Java cache read: historical four-component objects, modern strings/null/missing, unchanged raw/unknown data/current writes and twelve original invalid/reset boundaries. No Java executable or network.");
    }

    private static void Requirements(Fixture f) {
        void Check(string game, ModLoaders loader, string loaderVersion, string[] accepted, string[] rejected) {
            f.Game(game); f.Owner.Version.ModLoaderVersions[loader] = loaderVersion;
            ValueRange<Version> range = f.Engine.GetJavaRequirement(f.Owner).Range;
            foreach (string version in accepted) Require(range.Contains(new Version(version)), game + "/" + loaderVersion + " rejected " + version);
            foreach (string version in rejected) Require(!range.Contains(new Version(version)), game + "/" + loaderVersion + " accepted " + version);
        }
        Check("1.16.5", ModLoaders.Forge, "36.2.25", ["8.0.319", "8.0.320"], ["7.0", "8.0.321", "17.0"]);
        Check("1.16.5", ModLoaders.Forge, "36.2.26", ["8.0", "23.0"], ["7.0", "24.0"]);
        Check("1.17.1", ModLoaders.Forge, "37.0.79", ["16.0"], ["15.0", "17.0"]);
        Check("1.19.4", ModLoaders.Forge, "45.0.65", ["17.0", "19.0"], ["16.0", "20.0"]);
        Check("1.20.1", ModLoaders.Forge, "47.4.8", ["17.0", "21.0"], ["16.0", "22.0"]);
        Check("1.20.5", ModLoaders.Fabric, "0.16.14", ["21.0", "24.0"], ["20.0", "25.0"]);
        Check("1.20.5", ModLoaders.Fabric, "0.17.0", ["21.0", "25.0"], ["20.0"]);
        Check("1.20.2", ModLoaders.NeoForge, "20.2.62-beta", ["17.0", "21.0"], ["22.0"]);
        Check("1.12.2", ModLoaders.LiteLoader, "未知版本", ["8.0"], ["7.0", "9.0"]);
        f.Game("1.10.2"); f.Owner.Version.OptiFine = "H1";
        ValueRange<Version> optifine = f.Engine.GetJavaRequirement(f.Owner).Range;
        Require(optifine.Contains(new Version(8, 0)) && !optifine.Contains(new Version(9, 0)), "OptiFine's existing Java 8 interval changed.");
        f.Game("1.20.1"); f.Owner.Version.ModLoaderVersions[ModLoaders.Forge] = "47.4.8";
        f.Owner.Json["javaVersion"] = new JObject { ["majorVersion"] = 25, ["component"] = "future" };
        ValueRange<Version> conflict = f.Engine.GetJavaRequirement(f.Owner).Range;
        Require(!conflict.HasLower && conflict.Contains(new Version(7, 0)) && !conflict.Contains(new Version(22, 0)),
            "LF conflict handling must retain the later Forge constraint instead of inventing a new intersection policy.");
    }

    private static void Metadata(Fixture f) {
        f.Game("Unknown"); f.Owner.Version.ReleaseTime = new DateTime(2024, 4, 2);
        Require(f.Engine.GetJavaRequirement(f.Owner).Range.Contains(new Version(21, 0)) && !f.Engine.GetJavaRequirement(f.Owner).Range.Contains(new Version(20, 0)), "Snapshot release-date Java boundary changed.");
        f.Game("26.1"); f.Owner.Jar = new JObject { ["java_version"] = 25, ["java_component"] = "jar-runtime" };
        var jar = f.Engine.GetJavaRequirement(f.Owner);
        Equal("jar-runtime", jar.RecommendedComponent); Require(!jar.Range.Contains(new Version(24, 0)), "JAR metadata lower bound was ignored.");
        f.Owner.Json["javaVersion"] = new JObject { ["majorVersion"] = 26, ["component"] = "json-runtime" };
        var json = f.Engine.GetJavaRequirement(f.Owner);
        Equal("json-runtime", json.RecommendedComponent); Require(!json.Range.Contains(new Version(25, 0)), "Main JSON did not take precedence over JAR metadata.");
        f.Mode = 1; f.Range = "[8, 9)";
        var forced = f.Engine.GetJavaRequirement(f.Owner);
        Equal<string?>(null, forced.RecommendedComponent); Require(forced.Range.Contains(new Version(8, 0, 0, 0)) && !forced.Range.Contains(new Version(9, 0, 0, 0)), "Forced range did not override game metadata.");
        f.Range = "uninitialized range";
        Require(!f.Engine.GetJavaRequirement(f.Owner).Range.Contains(new Version(25, 0)), "Invalid forced range did not fall back to existing requirements.");
        f.Mode = 0; f.Game("1.5.2"); f.Owner.Version.ReleaseTime = new DateTime(2013, 5, 1); f.Nide = true;
        ValueRange<Version> nide = f.Engine.GetJavaRequirement(f.Owner).Range;
        Require(!nide.Contains(new Version(8, 0, 140)) && nide.Contains(new Version(8, 0, 141)) && !nide.Contains(new Version(9, 0)), "LF Nide 8u141 and old-game upper bound changed.");
    }

    private static void ManagedRetirement(Fixture f) {
        f.Game("1.7.10"); f.Owner.Version.ModLoaderVersions[ModLoaders.Forge] = "10.13.4.1614";
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, false, false).Range.Contains(new Version(21, 0)), "Missing proof retired ordinary Forge's Java8 bound.");
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, true, false).Range.Contains(new Version(21, 0)), "Minecraft-only retirement retired Forge as well.");
        Require(f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range.Contains(new Version(21, 0)), "Proven replacement retained the retired Forge upper bound.");
        f.Game("1.20.5"); f.Owner.Json["javaVersion"] = new JObject { ["majorVersion"] = 25, ["component"] = "future" };
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, false, true).Range.Contains(new Version(21, 0)), "Forge-only retirement retired Mojang metadata.");
        var minecraft = f.Engine.GetManagedJavaRequirement(f.Owner, true, false);
        Require(minecraft.Range.Contains(new Version(8, 0)) && minecraft.RecommendedComponent is null, "Minecraft retirement retained release/version/metadata inference.");
    }

    private static void ManagedHardBounds(Fixture f) {
        f.Game("1.7.10"); f.Nide = true; f.Mode = 1; f.Range = "[8, 9)";
        var account = f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range;
        Require(!account.Contains(new Version(8, 0, 140, 0)) && account.Contains(new Version(8, 0, 141, 0)) && !account.Contains(new Version(9, 0, 0, 0)), "User range early-return lost the account bound or the user's range.");
        var oldAccountReader = f.Host.IsNideLogin;
        f.Host.IsNideLogin = () => throw new Exception("Managed validation attempted live account IO instead of its snapshot.");
        try {
            Require(f.Engine.GetManagedJavaRequirement(f.Owner, true, true, nideLogin: false).Range.Contains(new Version(8, 0, 140, 0)), "A captured non-Nide account did not remain independent of live account state.");
            Require(!f.Engine.GetManagedJavaRequirement(f.Owner, true, true, nideLogin: true).Range.Contains(new Version(8, 0, 140, 0)), "A captured Nide account lost its retained minimum.");
        } finally { f.Host.IsNideLogin = oldAccountReader; }
        f.Nide = false; f.Mode = 0; f.Game("1.10.2"); f.Owner.Version.OptiFine = "H1";
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range.Contains(new Version(21, 0)), "Retirement removed an unrelated OptiFine bound.");
        f.Game("1.20.5"); f.Owner.Version.ModLoaderVersions[ModLoaders.Fabric] = "0.16.14";
        var fabric = f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range;
        Require(!fabric.Contains(new Version(16, 0)) && fabric.Contains(new Version(21, 0)) && !fabric.Contains(new Version(25, 0)), "Retirement removed Fabric's actual interval.");
        f.Game("1.20.2"); f.Owner.Version.ModLoaderVersions[ModLoaders.NeoForge] = "20.2.62-beta";
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range.Contains(new Version(22, 0)), "Retirement removed NeoForge's bound.");
        f.Game("1.12.2"); f.Owner.Version.ModLoaderVersions[ModLoaders.LiteLoader] = "1";
        Require(!f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range.Contains(new Version(9, 0)), "Retirement removed LiteLoader's bound.");
    }

    private static void ManagedConflicts(Fixture f) {
        f.Game("1.20.1"); f.Owner.Version.ModLoaderVersions[ModLoaders.Forge] = "47.4.8";
        f.Owner.Json["javaVersion"] = new JObject { ["majorVersion"] = 25 };
        Require(f.Engine.GetManagedJavaRequirement(f.Owner, false, false).Range.IsEmpty(), "Managed metadata/Forge conflict was replaced with the last bound.");
        Require(f.Engine.GetManagedJavaRequirement(f.Owner, true, false).Range.Contains(new Version(21, 0)), "Minecraft-only retirement failed to preserve the remaining Forge interval.");
        Require(f.Engine.GetManagedJavaRequirement(f.Owner, false, true).Range.Contains(new Version(25, 0)), "Forge-only retirement lost the remaining Minecraft interval.");
        Require(f.Engine.GetJavaRequirement(f.Owner).Range.Contains(new Version(8, 0)), "Ordinary conflict replacement changed.");
        f.Game("1.7.10"); f.Nide = true; f.Mode = 1; f.Range = "[7, 8)";
        Require(f.Engine.GetManagedJavaRequirement(f.Owner, true, true).Range.IsEmpty(), "A conflicting user/account interval became satisfiable.");
        Require(f.Engine.GetJavaRequirement(f.Owner).Range.Contains(new Version(7, 0, 0, 0)), "Ordinary explicit user range changed.");
    }

    private static void Migration(Fixture f) {
        // Empty legacy JSON exercises the actual old schema and config-version migration without executing a downloaded Java.
        Configs.JavaConfigVersion.Set(0); f.Engine.JavaInit(); f.WaitWorker();
        Equal(Versions.JavaConfigVersion, Configs.JavaConfigVersion.Get()); Equal(0, Configs.JavaList.Get()!.Count); Equal(1, f.Starts);
        f.OldInstanceJava = "{broken";
        Equal<Java?>(null, f.Engine.SelectOrDownloadJava(f.Owner, false));
        Equal(1, f.Disabled); Equal(1, f.Restored); Require(!f.UiDisabled && !Configs.JavaMigrated.Get(f.Owner.Config), "Malformed old Java setting marked migration done or left settings disabled.");
        f.OldInstanceJava = "";
        using (CancellationTokenSource canceled = new()) {
            canceled.Cancel();
            Throws<OperationCanceledException>(() => f.Engine.SelectOrDownloadJava(f.Owner, false, canceled.Token));
        }
        Equal(2, f.Disabled); Equal(2, f.Restored); Require(!f.UiDisabled && !Configs.JavaMigrated.Get(f.Owner.Config), "Canceled migration committed or failed to restore UI.");
        Java cached = f.CheckedJava("cached", "17.0.1.0"); Configs.JavaList.Set(new ConcurrentList<Java>([cached]));
        Equal(cached, f.Engine.SelectOrDownloadJava(f.Owner, false));
        Equal(3, f.Disabled); Equal(3, f.Restored); Equal(0, f.Mode); Require(Configs.JavaMigrated.Get(f.Owner.Config) && !f.UiDisabled, "Successful no-forced-setting migration was not committed.");
        f.Engine.SelectOrDownloadJava(f.Owner, false); Equal(3, f.Disabled); Equal(3, f.Restored);
        f.Owner.Config.Save(); f.Owner.Config.DiscardCache(); Require(Configs.JavaMigrated.Get(f.Owner.Config), "Migration flag did not round trip through the real JsonConfigProvider.");
        f.Provider.Save(); Require(File.Exists(f.GlobalConfigPath), "Java cache did not persist to its GUID profile.");
    }

    private static void Selection(Fixture f) {
        Configs.JavaMigrated.Set(true, f.Owner.Config); f.Game("1.20.5"); f.Mode = 3;
        Java forced = f.CheckedJava("forced", "8.0.401.0"); Java automatic = f.CheckedJava("automatic", "21.0.1.0");
        Configs.JavaList.Set(new ConcurrentList<Java>([automatic, forced])); Configs.JavaForced.Set(forced, f.Owner.Config);
        Require(ReferenceEquals(forced, f.Engine.SelectOrDownloadJava(f.Owner, false)), "Forced Java was replaced to satisfy an automatic version rule.");
        Equal(0, f.Starts);
        Configs.JavaForced.Reset(f.Owner.Config); Equal<Java?>(null, f.Engine.SelectOrDownloadJava(f.Owner, true));
        Equal("未选择 Java", f.Messages.Last().Title); Require(f.Messages.Last().Warn && f.Messages.Last().Wait, "Missing forced Java lost its synchronous warning."); Equal(0, f.Starts);
        Java invalid = f.UnavailableJava("removed"); Configs.JavaList.Set(new ConcurrentList<Java>([automatic, invalid])); Configs.JavaForced.Set(invalid, f.Owner.Config);
        int updates = f.Updates;
        Equal<Java?>(null, f.Engine.SelectOrDownloadJava(f.Owner, false)); Equal<Java?>(null, Configs.JavaForced.Get(f.Owner.Config)); Equal(updates + 1, f.Updates); Equal(0, f.Starts);
        f.Mode = 0; f.Game("1.18.2"); Java older = f.CheckedJava("first", "17.0.2.0");
        Configs.JavaList.Set(new ConcurrentList<Java>([invalid, forced, older, automatic]));
        Require(ReferenceEquals(older, f.Engine.SelectOrDownloadJava(f.Owner, false)), "Automatic selection reordered LF's stored preference list.");
        Equal(0, f.Starts);
        Configs.JavaRemovedList.Set([older.Folder.ToUpperInvariant()]); f.Engine.ManuallyRemoveJava(older);
        Equal(1, Configs.JavaRemovedList.Get()!.Count); Require(!Configs.JavaList.Get()!.Contains(older) && f.Reloaded == 1, "Removing Java lost its persistent exclusion or current-selection refresh.");
    }

    private static void Worker(Fixture f) {
        using ManualResetEventSlim entered = new(), release = new(), observed = new();
        f.ReplaceWorker(() => { entered.Set(); release.Wait(); });
        f.CurrentWorker.Start(); Require(entered.Wait(TimeSpan.FromSeconds(3)), "Real refresh worker did not enter.");
        f.Host.RefreshWorker = () => { observed.Set(); return f.CurrentWorker; };
        using CancellationTokenSource canceled = new();
        Task<Java?> choosing = Task.Run(() => f.Engine.SelectOrDownloadJava(ValueRange<Version>.All(), false, canceled.Token));
        try {
            Require(observed.Wait(TimeSpan.FromSeconds(3)), "Selection did not consult the real refresh worker."); canceled.Cancel();
            Require(!choosing.Wait(100), "LF's un-tokened initial worker wait was changed.");
        } finally { release.Set(); }
        Require(choosing.Wait(TimeSpan.FromSeconds(5)), "Selection remained blocked after refresh ended.");
        Equal<Java?>(null, choosing.GetAwaiter().GetResult()); f.WaitWorker();
        Java cached = f.CheckedJava("manual", "21.0.1.0"); Configs.JavaList.Set(new ConcurrentList<Java>([cached]));
        bool invalidated = false;
        f.ReplaceWorker(() => invalidated = cached.available == null); f.Host.RefreshWorker = () => f.CurrentWorker;
        f.Engine.ManuallySearchJava(); f.WaitWorker();
        Require(invalidated && f.Hints.Last().Kind == LegacyJavaHintKind.Green, "Manual search did not invalidate the actual Java cache before refreshing.");
        Configs.JavaMigrated.Set(true, f.Owner.Config); f.Mode = 2;
        using CancellationTokenSource direct = new(); direct.Cancel();
        Throws<OperationCanceledException>(() => f.Engine.SelectOrDownloadJava(f.Owner, false, direct.Token));
    }

    private static void Download(Fixture f) {
        string index = "{\"windows-x64\":{\"minecraft-java-exe\":[{\"version\":{\"name\":\"99\"}}],\"java-eight\":[{\"version\":{\"name\":\"8u51-cacert462b08\"}}],\"java-seventeen\":[{\"version\":{\"name\":\"17.0.1.12.1\"},\"manifest\":{\"url\":\"https://piston-meta.mojang.com/fixture.json\"}}],\"java-twenty-one\":[{\"version\":{\"name\":\"21\"}}],\"broken\":[{\"version\":{\"name\":\"-1.0\"}}],\"empty\":[]},\"windows-x86\":{\"ignored\":[]}}";
        List<LegacyJavaRuntime> runtimes = LegacyJavaService<Owner>.ParseRuntimeIndex(index);
        Equal(3, runtimes.Count); Equal(new Version(8, 0, 51, 0), runtimes[0].JavaVersion); Equal(new Version(17, 0, 1, 12), runtimes[1].JavaVersion); Equal(new Version(21, 0, 0, 0), runtimes[2].JavaVersion);
        using (CancellationTokenSource canceled = new()) { canceled.Cancel(); Throws<OperationCanceledException>(() => LegacyJavaService<Owner>.ParseRuntimeIndex(index, canceled.Token)); }
        JObject files = new() { ["bin/java.exe"] = Raw("kept-sha1", 321), ["link"] = new JObject { ["type"] = "link" } };
        int number = 0; foreach (string hash in new[] { "12976a6c2b227cbac58969c1455444596c894656", "c80e4bab46e34d02826eab226a4441d0970f2aba", "84d2102ad171863db04e7ee22a259d1f6c5de4a5" }) files["ignored" + number++] = Raw(hash, 9);
        string manifest = new JObject { ["files"] = files }.ToString();
        int indices = 0, manifests = 0, waits = 0, cancels = 0;
        f.Host.RequestJson = urls => {
            string first = urls.First();
            if (first.EndsWith("all.json", StringComparison.Ordinal)) { indices++; return index; }
            Require(first == "https://bmclapi2.bangbang93.com/fixture.json", "Manifest mirror source order changed."); manifests++; return manifest;
        };
        LegacyJavaDownloadPlan plan = f.Engine.CreateDownloadPlan(runtimes[1]);
        Equal(1, plan.Files.Count); Equal(321L, plan.Files[0].ActualSize); Equal("kept-sha1", plan.Files[0].Hash);
        Require(plan.Files[0].Destination == plan.BaseDirectory + "bin/java.exe" && plan.Files[0].Sources.First().Contains("bmclapi2.bangbang93.com", StringComparison.Ordinal), "Raw manifest path or mirror priority changed.");
        string stage = Path.Combine(f.Root, "owned-incomplete"); Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "partial"), "partial");
        new LegacyJavaDownloadPlan { BaseDirectory = stage }.Incomplete(); Require(!Directory.Exists(stage), "Incomplete Java staging was not removed.");
        f.Host.CreateDownload = download => new LegacyJavaDownloadOperation {
            Wait = (_, _) => { waits++; throw new IOException("synthetic loader failure"); }, Cancel = () => cancels++
        };
        ValueRange<Version> range = ValueRange<Version>.ClosedOpen(new Version(17, 0), new Version(18, 0));
        Equal<Java?>(null, f.Engine.SelectOrDownloadJava(range, true)); Equal(1, waits); Equal(1, cancels); Equal(1, indices);
        using CancellationTokenSource stopped = new();
        f.Host.CreateDownload = download => new LegacyJavaDownloadOperation { Wait = (token, _) => { waits++; stopped.Cancel(); token.ThrowIfCancellationRequested(); }, Cancel = () => cancels++ };
        Throws<OperationCanceledException>(() => f.Engine.SelectOrDownloadJava(range, true, stopped.Token));
        Equal(2, waits); Equal(2, cancels); Equal(1, indices); Equal(3, manifests);
        static JObject Raw(string hash, long size) => new() { ["downloads"] = new JObject { ["raw"] = new JObject { ["url"] = "https://piston-data.mojang.com/java.exe", ["size"] = size, ["sha1"] = hash } } };
    }

    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action work) where T : Exception { try { work(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    // This is only the typed VB/platform payload. Minecraft versions, Java entries, configs, workers and all rules are production types.
    private sealed class Owner(string root) {
        internal readonly string Folder = Path.Combine(root, "instance");
        internal readonly JsonConfigProvider Config = new(Path.Combine(root, "instance-config.json"));
        internal McVersion Version = new("1.18.2") { ReleaseTime = new DateTime(2022, 1, 1) };
        internal JObject Json = [];
        internal JObject? Jar;
    }
    private sealed class Fixture : IDisposable {
        internal readonly string Root, GlobalConfigPath;
        private readonly string oldDirectory;
        internal readonly JsonConfigProvider Provider;
        internal readonly Owner Owner;
        internal readonly LegacyJavaHost<Owner> Host;
        internal readonly LegacyJavaService<Owner> Engine;
        private readonly List<RedoableWorker> workers = [];
        internal RedoableWorker CurrentWorker = null!;
        internal int Mode, Disabled, Restored, Starts, Updates, Reloaded;
        internal bool UiDisabled, Nide;
        internal string Range = "", OldInstanceJava = "";
        internal readonly List<(string Text, string Title, bool Warn, bool Wait)> Messages = [];
        internal readonly List<(string Text, LegacyJavaHintKind Kind)> Hints = [];
        internal Fixture() {
            Root = Path.Combine(Path.GetTempPath(), "LivingPCL-Java-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            oldDirectory = Environment.CurrentDirectory; Environment.CurrentDirectory = Root;
            GlobalConfigPath = Path.Combine(Root, "global-config.json");
            Provider = new JsonConfigProvider(GlobalConfigPath); ConfigProviders.Default = Provider;
            Require(ReferenceEquals(Provider, Configs.JavaList.Provider) && ReferenceEquals(Provider, Configs.JavaRemovedList.Provider) && ReferenceEquals(Provider, Configs.JavaConfigVersion.Provider), "Java entries captured a different profile; refusing to write external settings.");
            Configs.JavaList.Set(new ConcurrentList<Java>()); Configs.JavaRemovedList.Set([]); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            Owner = new Owner(Root); Directory.CreateDirectory(Owner.Folder); ReplaceWorker(() => { });
            Host = new LegacyJavaHost<Owner> {
                ReadString = (key, _) => key switch { "LaunchArgumentJavaAll" => "[]", "LaunchArgumentJavaSelect" => "", "VersionArgumentJavaSelect" => OldInstanceJava, "VersionArgumentJavaRange" => Range, _ => throw new InvalidOperationException("Unexpected Java setting: " + key) },
                ReadInteger = (key, _) => key == "VersionArgumentJavaV2" ? Mode : throw new InvalidOperationException(key),
                WriteInteger = (key, value, _) => { Require(key == "VersionArgumentJavaV2", "Unexpected setting write."); Mode = value; },
                Config = instance => instance.Config, Version = instance => instance.Version, VersionPath = instance => instance.Folder,
                InstanceName = _ => "instance", VersionDisplayName = instance => instance.Version.VanillaName,
                ReleaseTime = instance => instance.Version.ReleaseTime, JsonObject = instance => instance.Json, JsonVersion = instance => instance.Jar,
                IsNideLogin = () => Nide, RefreshWorker = () => CurrentWorker, UpdateJavaLists = () => Updates++,
                SelectJavaExecutable = () => throw new InvalidOperationException("No live Java dialog is allowed."),
                Hint = (text, kind) => Hints.Add((text, kind)), ReloadCurrentJava = () => Reloaded++, RunInThread = work => work(),
                SwitchToInstanceSetup = _ => { }, Message = (text, title, warn, wait) => Messages.Add((text, title, warn, wait)),
                DisableJavaSettings = () => { Disabled++; UiDisabled = true; }, RestoreJavaSettings = () => { Restored++; UiDisabled = false; },
                VersionListOrder = (official, mirror) => mirror.Concat(official), SourceOrder = (official, mirror) => mirror.Concat(official),
                RequestJson = _ => throw new InvalidOperationException("No live Java request is allowed."),
                CreateDownload = _ => throw new InvalidOperationException("No live Java download is allowed.")
            };
            Engine = new LegacyJavaService<Owner>(Host);
        }
        internal void Game(string name) { Owner.Version = new McVersion(name) { ReleaseTime = new DateTime(2022, 1, 1) }; Owner.Json = []; Owner.Jar = null; }
        internal Java UnavailableJava(string name) {
            Java java = new(Path.Combine(Root, name), new Version(17, 0, 0, 0));
            Require(!java.CheckAsync().Run(), "An absent fixture java.exe unexpectedly exists."); return java;
        }
        internal Java CheckedJava(string name, string version) {
            Java java = UnavailableJava(name); java.Version = new Version(version);
            // A pre-checked platform cache entry: the actual checked-state transition above is real.
            // This does not claim that a java.exe process or its architecture was executed/validated.
            java.available = true; return java;
        }
        internal void ReplaceWorker(Action work) {
            RedoableWorker worker = new(work); worker.Started += () => Interlocked.Increment(ref Starts); workers.Add(worker); CurrentWorker = worker;
        }
        internal void WaitWorker() => Require(CurrentWorker.WaitIfRunning(5000), "Real refresh worker did not finish.");
        public void Dispose() {
            try {
                foreach (RedoableWorker worker in workers) { worker.Cancel(); Require(worker.WaitIfRunning(5000), "Fixture left a running Java worker."); }
                Owner.Config.Save(); Provider.Save();
            } finally {
                Environment.CurrentDirectory = oldDirectory;
                string full = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Require(full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "\\ALivingPCL-Java-[0-9a-f]{32}\\z"), "Unsafe Java fixture cleanup path.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
    }
}
