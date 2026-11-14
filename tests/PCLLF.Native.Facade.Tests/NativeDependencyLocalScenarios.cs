using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;
internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static void Local(FixtureEnvironment environment)
    {
        localCount = 0;
        LocalSeen.Clear();
        using Scope f = new(environment, "dependency-local");
        L(99, Native.Main.GetType("PCL.ModDependencyIndex")is not null);
        L(104, (bool)Native.Call(Inspector, "SameFilePath", null, @"\\?\UNC\server\share\mods\example.jar", @"\\server\share\mods\example.jar")!);
        L(105, !(bool)Native.Call(Inspector, "SameFilePath", null, @"\\?\UNC\server\share\mods\example.jar", @"\\server\other-share\mods\example.jar")!);
        string longPath = @"C:\" + string.Concat(Enumerable.Repeat("long-folder-", 24)) + @"\example.jar";
        L(108, (string)Native.Call(Inspector, "FilePathKey", null, @"\\?\" + longPath)! == longPath);
        LocalVersions();
        string folder = f.Folder("fabric");
        string core = Fabric(folder, "core.jar", "core", "1.2.0", Obj("""{"provides":["api_alias"]}"""));
        string addon = Fabric(folder, "addon.jar", "addon", "1.0", Obj("""{"depends":{"api_alias":[">=1.0 <2.0",">=3.0"],"minecraft":"1.20.1","java":">=17","fabricloader":">=0.15"},"recommends":{"recommendation":"*"},"suggests":{"suggested":"*"}}"""));
        string consumer = Fabric(folder, "consumer.jar", "consumer", "1.0", Obj("""{"depends":{"addon":"*"}}"""));
        Fabric(folder, "server.jar", "server", "1.0", Obj("""{"environment":"server","depends":{"server_only_missing":"*"}}"""));
        string disabled = Fabric(folder, "disabled.jar.disabled", "disabled", "1.0", Obj("""{"depends":{"disabled_only_missing":"*"}}"""));
        Fabric(folder, "legacy.jar.old", "legacy", "1.0");
        TReport report = Scan(folder);
        L(155, !report.HasErrors);
        L(156, Issues(report, "Recommendation").Length == 1);
        L(157, Issues(report, "MissingRequired").Length == 0);
        string uiCore = Extended(core), uiAddon = Extended(addon);
        L(161, report.DescribeFile(uiAddon) == report.DescribeFile(addon));
        L(162, report.DescribeFile(Extended(disabled)) == report.DescribeFile(disabled));
        var impacts = Impacts(report, uiCore);
        L(164, impacts.Count(i => i.Mod.Id == "addon" && i.IsDefinite) == 1);
        var extendedReport = Scan(Extended(folder));
        L(166, extendedReport.DescribeFile(addon) == report.DescribeFile(addon));
        impacts = Impacts(extendedReport, core, uiAddon);
        L(168, impacts.Count(i => i.Mod.Id == "consumer") == 1);
        L(169, impacts.All(i => i.Mod.Id != "addon"));
        L(170, report.Edges.Count >= 6 && report.ReverseDependencies.Count >= 3);
        string caption = report.DescribeFile(core);
        L(172, caption.Contains("addon") && caption.Contains("被这些模组依赖"));
        L(173, report.ToSummary().Contains("确定问题：0"));
        impacts = Impacts(report, core);
        L(175, impacts.Count == 2);
        L(176, impacts.Count(i => i.Mod.Id == "addon" && i.IsDirect && i.IsDefinite) == 1);
        L(177, impacts.Count(i => i.Mod.Id == "consumer" && !i.IsDirect) == 1);
        L(178, Impacts(report, core, addon, consumer).Count == 0);
        L(179, DescribeImpacts(report, core).Contains("间接受影响"));
        L(180, Impacts(report, disabled).Count == 0);
        var before = Directory.GetFiles(folder).ToDictionary(path => path, path => Hash(path, "SHA256"));
        Scan(folder);
        foreach (var pair in before)
            L(183, Hash(pair.Key, "SHA256") == pair.Value);
        File.Move(core, core + ".moved");
        File.Move(core + ".moved", core);
        folder = f.Folder("missing");
        Fabric(folder, "consumer.jar", "consumer", "1.0", Obj("""{"depends":{"library":">=1.0"}}"""));
        Fabric(folder, "library.jar.disabled", "library", "1.5");
        Fabric(folder, "library_extra.jar", "library_extra", "1.5");
        report = Scan(folder);
        var missing = Issues(report, "MissingRequired");
        L(191, missing.Length == 1 && missing[0].IsError && missing[0].Message.Contains("已禁用"));
        Fabric(folder, "duplicate-one.jar", "duplicate", "1.0");
        Fabric(folder, "duplicate-two.jar", "duplicate", "2.0");
        Fabric(folder, "duplicate-disabled.jar.disabled", "duplicate", "3.0");
        report = Scan(folder);
        var duplicates = Issues(report, "DuplicateId");
        L(196, duplicates.Length == 1 && duplicates[0].RelatedMods.Count == 2);
        folder = f.Folder("fabric-id");
        Fabric(folder, "consumer.jar", "consumer", "1.0", Obj("""{"depends":{"fabric":"*"}}"""));
        L(200, Issues(Scan(folder), "MissingRequired").Length == 1);
        Fabric(folder, "fabric-api.jar", "fabric", "1.0");
        L(202, !Scan(folder).HasErrors);
        folder = f.Folder("version-errors");
        Fabric(folder, "lib.jar", "lib", "1.20.10");
        Fabric(folder, "consumer.jar", "consumer", "1.0", Obj("""{"depends":{"lib":"1.20.1"}}"""));
        Fabric(folder, "unknown.jar", "unknown", "${version}");
        Fabric(folder, "dynamic.jar", "dynamic", "1.0", Obj("""{"depends":{"unknown":">=2.0","lib":"latest.release"}}"""));
        report = Scan(folder);
        var mismatch = Issues(report, "VersionMismatch");
        L(210, mismatch.Length == 1 && mismatch[0].Owner.Id == "consumer");
        var unknownContext = Context();
        unknownContext.JavaVersion = null;
        Fabric(folder, "java.jar", "java_requirement", "1.0", Obj("""{"depends":{"java":">=21"}}"""));
        report = Scan(folder, unknownContext);
        L(214, Issues(report, "MissingRequired", "java_requirement").Length == 0 && Issues(report, "VersionMismatch", "java_requirement").Length == 0);
        report = Scan(folder);
        L(216, Issues(report, "VersionMismatch", "java_requirement").Length == 1);
        LocalTextAndNested(f);
        LocalLoaderAndLegacy(f);
        LocalInstanceContext();
        LocalIndex(f, environment);
        LocalMutationPreview(f, environment);
        var expected = NativeDependencyOriginalAssertions.Local.Keys.Except(new[] { 620, 624 }).ToHashSet();
        Native.Check(LocalSeen.SetEquals(expected), "Local source assertion coverage mismatch: missing=" + string.Join(',', expected.Except(LocalSeen)) + " extra=" + string.Join(',', LocalSeen.Except(expected)));
        Console.WriteLine("PASS native dependency-local: " + localCount + " assertions across " + LocalSeen.Count + " fixed-LF50 source statements; external real-pack opt-in620/624 was not selected.");
    }

    private static void LocalVersions()
    {
        (string Actual, string Range, string State)[] fabric = [("1.2.3", ">=1.2.0 <2.0", "Satisfied"), ("1.2.3", ">= 1.2.0 < 2.0", "Satisfied"), ("2.0", ">=1.2.0 <2.0", "Unsatisfied"), ("1.20.10", "1.20.1", "Unsatisfied"), ("1.2.3", "1.2.x", "Satisfied"), ("1.3.0", "1.2.*", "Unsatisfied"), ("1.2.3", "~1.2", "Satisfied"), ("1.3.0", "~1.2", "Unsatisfied"), ("0.2.9", "^0.2.1", "Satisfied"), ("0.3", "^0.2.1", "Satisfied"), ("0.9.0", "^0.2.1", "Satisfied"), ("1.0.0-alpha", "^0.2.1", "Unsatisfied"), ("1.0", "1.0.0", "Satisfied"), ("1.0.5", "~1", "Satisfied"), ("1.1", "~1", "Unsatisfied"), ("1.3.0-alpha", "~1.2", "Unsatisfied"), ("1.0.0-rc.1", ">=1.0.0", "Unsatisfied"), ("1.0.0", ">1.0.0-rc.1", "Satisfied"), ("2.0", "1.0 || >=2.0", "Satisfied"), ("1.5", "1.0 || >=2.0", "Unsatisfied"), ("1.0", "latest.release", "Unknown"), ("1.0", "1.+", "Unknown"), ("24w14a", ">=1.20.1", "Unknown"), ("24w14a", "24w14a", "Satisfied"), ("24w14a", "1.20.1", "Unsatisfied"), ("1.0", ">=v1.0", "Unknown"), ("unknown", ">=1.0", "Unknown"), ("1.0", "1.0 - 2.0", "Unknown"), ("unknown", "1.0", "Unknown")];
        (string Actual, string Range, string State)[] maven = [("1.5", "[1.0,2.0)", "Satisfied"), ("2.0", "[1.0,2.0)", "Unsatisfied"), ("1.0", "(1.0,2.0]", "Unsatisfied"), ("1.20.1", "[1.20.1]", "Satisfied"), ("1.20.10", "[1.20.1]", "Unsatisfied"), ("1.0", "(,1.0],[2.0,)", "Satisfied"), ("1.5", "(,1.0],[2.0,)", "Unsatisfied"), ("1.0-final", "[1.0]", "Unknown"), ("1.0", "[1.0-final]", "Unknown"), ("1.0+build.1", "(1.0,2.0)", "Unknown"), ("2.0-beta", "[1.0,2.0)", "Unknown"), ("2.0", "1.0", "Unknown"), ("2.0", "[1.0,${version})", "Unknown")];
        foreach (var pair in new[]
        {
            ("fabric", fabric),
            ("maven", maven)
        }

        )
            foreach (var item in pair.Item2)
            {
                string actual = Native.Call(Inspector, "MatchVersion", null, item.Actual, item.Range, pair.Item1)!.ToString()!;
                L(124, actual == item.State, $"{pair.Item1}: {item.Actual} / {item.Range} expected {item.State} got {actual}");
            }
    }

    private static void LocalTextAndNested(Scope f)
    {
        string folder = f.Folder("quilt");
        Quilt(folder, "alias.jar", "provider", Obj("""{"provides":[{"id":"fixture:virtual","version":"2.5"}]}"""));
        Quilt(folder, "consumer.jar", "quilt_consumer", Obj("""{"depends":[{"id":"fixture:virtual","versions":{"all":[">=2.0","<3.0"]}},["missing_alternative","fixture:virtual"],{"id":"optional_absent","optional":true},{"id":"unless_absent","unless":"provider"}],"breaks":[{"id":"provider","versions":"*","unless":"fixture:virtual"}]}"""));
        Quilt(folder, "server.jar", "quilt_server", Obj("""{"depends":["server_absent"]}"""), Obj("""{"minecraft":{"environment":"dedicated_server"}}"""));
        TReport report = Scan(folder, Context("quilt_loader", "0.28.0"));
        L(226, !report.HasErrors, report.ToSummary());
        Quilt(folder, "bad.jar", "quilt_bad", Obj("""{"depends":[{"id":"fixture:virtual","versions":{"any":["<2.0",">=3.0"]}}]}"""));
        L(229, Issues(Scan(folder, Context("quilt_loader", "0.28.0")), "VersionMismatch", "quilt_bad").Length == 1);
        folder = f.Folder("forge");
        string toml = NativeDependencyLocalData.ForgeMulti.Replace("FILE_VERSION", "${file.jarVersion}");
        Jar(folder, "multi.jar", Entries(("META-INF/mods.toml", toml), ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0" + Environment.NewLine + "Implementation-Version: 1.2.3" + Environment.NewLine)));
        report = Scan(folder, Context("forge", "47.2.0"));
        L(272, report.Mods.Count == 2 && !report.HasErrors);
        L(273, report.Mods.Single(m => m.Id == "multi_one").Version == "1.2.3");
        L(274, report.Edges.All(e => e.Requirement.Id != "fake_from_description"));
        Jar(folder, "optional.jar", Entries(("META-INF/mods.toml", "[[mods]]\nmodId=\"optional_library\"\nversion=\"1.0\"")));
        string optional = NativeDependencyLocalData.ForgeOptional;
        Jar(folder, "optional-owner.jar", Entries(("META-INF/mods.toml", optional)));
        report = Scan(folder, Context("forge", "47.2.0"));
        L(289, Issues(report, "VersionMismatch", "optional_owner").Length == 1);
        string exactLanguage = optional.Replace("modId=\"optional_owner\"", "modId=\"language_owner\"").Replace("dependencies.optional_owner", "dependencies.language_owner").Replace("loaderVersion=\"[47,)\"", "loaderVersion=\"[47]\"");
        Jar(folder, "language.jar", Entries(("META-INF/mods.toml", exactLanguage)));
        report = Scan(folder, Context("forge", "47.2.0"));
        L(293, report.Issues.All(i => i.Owner.Id != "language_owner" || i.Requirement.Id != "javafml"));
        folder = f.Folder("neoforge");
        string neo = NativeDependencyLocalData.Neo;
        Jar(folder, "neo.jar", Entries(("META-INF/neoforge.mods.toml", neo)));
        report = Scan(folder, Context("neoforge", "21.1.100"));
        L(316, !report.HasErrors);
        Jar(folder, "bad.jar", Entries(("META-INF/neoforge.mods.toml", "[[mods]]\nmodId=\"bad\"\nversion=\"1.5\"")));
        L(319, Issues(Scan(folder, Context("neoforge", "21.1.100")), "Incompatible", "neo").Length == 1);
        folder = f.Folder("nested");
        string embedded = f.Folder("embedded");
        string nestedFile = Fabric(embedded, "nested.jar", "nested_api", "1.0");
        string parent = Jar(folder, "parent.jar", Entries(("fabric.mod.json", """{"schemaVersion":1,"id":"parent","version":"1.0","depends":{"nested_api":">=1.0"},"jars":[{"file":"META-INF/jars/nested.jar"}]}"""), ("META-INF/jars/nested.jar", File.ReadAllBytes(nestedFile))));
        Fabric(folder, "dependent.jar", "dependent", "1.0", Obj("""{"depends":{"nested_api":">=1.0"}}"""));
        report = Scan(folder);
        L(328, report.Mods.Count == 3 && !report.HasErrors && report.Mods.Count(m => m.IsNested) == 1);
        var impacts = Impacts(report, parent);
        L(330, impacts.Count == 1 && impacts[0].Mod.Id == "dependent");
        Fabric(folder, "external.jar", "nested_api", "2.0");
        report = Scan(folder);
        L(333, Issues(report, "DuplicateId").Length == 0);
        L(334, Impacts(report, parent).Count == 0);
        string quiltNested = Quilt(embedded, "quilt-inner.jar", "quilt_inner");
        string qp = Jar(folder, "quilt-parent.jar", Entries(("quilt.mod.json", """{"schema_version":1,"quilt_loader":{"group":"fixture","id":"quilt_parent","version":"1.0","jars":["META-INF/jars/quilt.jar"],"depends":["quilt_inner"]}}"""), ("META-INF/jars/quilt.jar", File.ReadAllBytes(quiltNested))));
        report = Scan(folder, Context("quilt_loader", "0.28"));
        L(339, report.Mods.Count(m => m.Id == "quilt_inner") == 1);
        L(341, Embedded(report, qp).Count == 1);
        folder = f.Folder("embedded-display");
        var parts = new Dictionary<string, JObject>
        {
            ["source_part"] = Obj("""{"contact":{"sources":"https://source.example/team/toolkit.git"}}"""),
            ["object_part"] = Obj("""{"custom":{"modmenu":{"parent":{"id":"toolkit_alias"}}}}"""),
            ["string_part"] = Obj("""{"custom":{"modmenu":{"parent":"toolkit"}}}"""),
            ["generated_part"] = Obj("""{"custom":{"fabric-loom:generated":true}}"""),
            ["independent_library"] = Obj("""{"contact":{"sources":"https://source.example/team/library","homepage":"https://team.example"}}"""),
            ["toolkit_independent"] = Obj("""{"contact":{"homepage":"https://team.example"}}"""),
            ["invalid_hint"] = Obj("""{"custom":{"modmenu":{"parent":123},"fabric-loom:generated":"true"},"contact":{"sources":"not a URL"}}""")
        };
        var entries = new Dictionary<string, object>();
        JArray jars = new();
        JObject requires = new();
        foreach (var pair in parts)
        {
            string inner = Fabric(embedded, pair.Key + ".jar", pair.Key, "1.0", pair.Value);
            string path = "META-INF/jars/" + pair.Key + ".jar";
            entries[path] = File.ReadAllBytes(inner);
            jars.Add(new JObject { ["file"] = path });
            requires[pair.Key] = ">=1";
        }

        JObject toolkitData = Obj("""{"schemaVersion":1,"id":"toolkit","version":"1","provides":["toolkit_alias"],"contact":{"sources":"https://source.example/team/toolkit/","homepage":"https://team.example"}}""");
        toolkitData["depends"] = requires;
        toolkitData["jars"] = jars;
        entries["fabric.mod.json"] = toolkitData.ToString();
        string toolkit = Jar(folder, "toolkit.jar", entries);
        report = Scan(folder);
        var shown = Embedded(report, toolkit);
        L(363, !report.HasErrors && shown.Count == 3 && shown.Any(m => m.Id == "independent_library") && shown.Any(m => m.Id == "toolkit_independent") && shown.Any(m => m.Id == "invalid_hint"));
        L(364, report.Mods.Count(m => m.Id == "generated_part" && m.IsGeneratedLibrary) == 1 && report.Mods.Count(m => m.Id == "object_part" && m.DisplayParentId == "toolkit_alias") == 1);
        ClearIndex();
        var cached = Scan(folder);
        L(367, cached.IndexHit && Embedded(cached, toolkit).Count == 3);
        Fabric(folder, "bad-consumer.jar", "bad_consumer", "1", Obj("""{"depends":{"source_part":">=2"}}"""));
        var broken = Scan(folder);
        L(371, broken.HasErrors && Issues(broken, "VersionMismatch", "bad_consumer").Length == 1);
        folder = f.Folder("unused-nested");
        string unused = Fabric(embedded, "unused.jar", "unused", "1.0", Obj("""{"depends":{"candidate_missing":"*"}}"""));
        Jar(folder, "parent.jar", Entries(("fabric.mod.json", """{"schemaVersion":1,"id":"candidate_parent","version":"1.0","breaks":{"unused":"*"},"jars":[{"file":"META-INF/jars/unused.jar"}]}"""), ("META-INF/jars/unused.jar", File.ReadAllBytes(unused))));
        report = Scan(folder);
        L(378, !report.HasErrors && report.Mods.Count(m => m.Id == "unused" && m.IsCandidate) == 1);
        folder = f.Folder("jarjar");
        string innerToml = NativeDependencyLocalData.JarJarInner;
        string ji = Jar(embedded, "jarjar.jar", Entries(("META-INF/neoforge.mods.toml", innerToml)));
        string outerNeo = neo.Replace("modId=\"bad\"", "modId=\"jarjar_library\"").Replace("type=\"incompatible\"", "type=\"required\"");
        string jouter = Jar(folder, "outer.jar", Entries(("META-INF/neoforge.mods.toml", outerNeo), ("META-INF/jarjar/metadata.json", """{"jars":[{"path":"META-INF/jarjar/library.jar"}]}"""), ("META-INF/jarjar/library.jar", File.ReadAllBytes(ji))));
        report = Scan(folder, Context("neoforge", "21.1.1"));
        L(392, !report.HasErrors && report.Mods.Count(m => m.Id == "jarjar_library") == 1);
        L(393, Embedded(report, jouter).Count == 1);
        folder = f.Folder("forge-jarjar-display");
        string fi = Jar(embedded, "forge-display.jar", Entries(("META-INF/mods.toml", innerToml.Replace("[4,)", "[47,)"))));
        string fo = outerNeo.Replace("modId=\"neo\"", "modId=\"forge_host\"").Replace("dependencies.neo", "dependencies.forge_host").Replace("modId=\"neoforge\"", "modId=\"forge\"").Replace("[21.1,22)", "[47,)").Replace("[4,)", "[47,)");
        string forgeJar = Jar(folder, "outer.jar", Entries(("META-INF/mods.toml", fo), ("META-INF/jarjar/metadata.json", """{"jars":[{"path":"META-INF/jarjar/library.jar"}]}"""), ("META-INF/jarjar/library.jar", File.ReadAllBytes(fi))));
        report = Scan(folder, Context("forge", "47.1.0"));
        L(399, Embedded(report, forgeJar).Count == 1 && report.Mods.Count(m => m.IsNested) == 1);
    }

    private static void LocalMutationPreview(Scope f, FixtureEnvironment environment)
    {
        var owner = BackupResourceChecks.Instance(f.Folder("preview/.minecraft/versions/preview"));
        owner.Version = new McVersion("1.20.1", true);
        string folder = Path.Combine(owner.PathIndie, "mods");
        string core = Fabric(folder, "core.jar", "core", "1.0");
        string user = Fabric(folder, "user.jar", "user", "1.0", Obj("""{"depends":{"core":"*"}}"""));
        Type runtime = Native.Type("PCL.LocalModPageRuntime");
        IList impacts = (IList)Native.Call(runtime, "InspectImpacts", null, owner, new[] { core })!;
        Native.Check(impacts.Count == 1, "The actual mod-page removal handler lost an ordinary instance's local dependency warning.");
        using (FileStream lease = new(user, FileMode.Open, FileAccess.Read, FileShare.None)) {
            File.Move(core, core + ".disabled");
            var disabled = (TReport)Native.Call(Inspector, "InspectForImpact", null, folder,
                Native.Call(Inspector, "ContextForInstance", null, owner, null)!, CancellationToken.None)!;
            Native.Check(disabled.HasErrors && disabled.IndexArchivesRead == 0 && !disabled.IndexReadFailed,
                "The actual removal facade reopened the unchanged consumer after disabling its provider.");
            File.Move(core + ".disabled", core);
            impacts = (IList)Native.Call(runtime, "InspectImpacts", null, owner, new[] { core })!;
            Native.Check(impacts.Count == 1, "Reenabling the cached provider lost its local removal impact.");
        }
        TReport cached = Scan(folder);
        cached.OnlineFiles.Add(new TOnlineFile { FullPath = user, Enabled = true, IsSupplementTarget = true,
            Matches = new List<TOnlineProject> { new() { Platform = "Modrinth", IsExact = true, Dependencies = new() } } });
        impacts = (IList)Native.Call(Native.Type("PCL.ModDependencyImpactView"), "Collect", null, cached, new[] { core })!;
        Native.Check(impacts.Count == 1, "An exact cached platform project hid a required local dependency.");
        environment.AssertNoForbiddenIo();
    }

    private static void LocalLoaderAndLegacy(Scope f)
    {
        string folder = f.Folder("uncertain");
        Fabric(folder, "consumer.jar", "consumer", "1.0", Obj("""{"depends":{"missing":"*"}}"""));
        Jar(folder, "unknown.jar", Entries(("fixture.txt", "not a recognised metadata format")));
        var report = Scan(folder);
        L(405, report.MetadataIncomplete && !report.HasErrors && Issues(report, "MissingRequired").Length == 1);
        File.WriteAllText(Path.Combine(folder, "corrupt.jar"), "not a ZIP");
        report = Scan(folder);
        L(408, report.MetadataIncomplete && !report.HasErrors);
        Jar(folder, "invalid-utf8.jar", Entries(("fabric.mod.json", new byte[] { 255, 255 })));
        report = Scan(folder);
        L(411, report.MetadataIncomplete && !report.HasErrors);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            bool rejected = false;
            try
            {
                Scan(folder, token: cancel.Token);
            }
            catch (OperationCanceledException)
            {
                rejected = true;
            }

            L(414, rejected);
        }

        folder = f.Folder("loader-mismatch");
        string foreignToml = "modLoader=\"javafml\"\nloaderVersion=\"[47,)\"\n[[mods]]\nmodId=\"foreign\"\nversion=\"1\"\n[[dependencies.foreign]]\nmodId=\"foreign_only_dependency\"\nmandatory=true";
        string foreign = Jar(folder, "forge-only.jar", Entries(("META-INF/mods.toml", foreignToml)));
        Jar(folder, "neo-only.jar", Entries(("META-INF/neoforge.mods.toml", foreignToml.Replace("foreign", "neo_foreign"))));
        Jar(folder, "disabled.jar.disabled", Entries(("META-INF/mods.toml", foreignToml)));
        Jar(folder, "universal.jar", Entries(("META-INF/mods.toml", foreignToml), ("fabric.mod.json", """{"schemaVersion":1,"id":"universal","version":"1"}""")));
        report = Scan(folder);
        var wrong = Issues(report, "LoaderMismatch");
        L(423, wrong.Length == 2 && Issues(report, "MissingRequired").Length == 0 && Issues(report, "DuplicateId").Length == 0);
        L(424, wrong[0].Message.Contains("禁用") && wrong[0].Message.Contains("Fabric"));
        L(426, !string.IsNullOrEmpty(Warning(folder, foreign)));
        L(427, string.IsNullOrEmpty(Warning(folder, foreign, Context("forge", "47.2.0"))));
        byte[] old = File.ReadAllBytes(foreign);
        File.WriteAllBytes(foreign, [..old, 0]);
        L(429, string.IsNullOrEmpty(Warning(folder, foreign)));
        File.WriteAllBytes(foreign, old);
        report = Scan(folder);
        var requirement = new TDependency
        {
            Id = "foreign"
        };
        L(432, !(bool)Native.Call(Inspector, "FileSatisfiesDependency", null, report, foreign, requirement)!);
        report = Scan(folder, Context("forge", "47.2.0"));
        L(434, Issues(report, "LoaderMismatch", "neo_foreign").Length == 1 && Issues(report, "LoaderMismatch", "foreign").Length == 0);
        folder = f.Folder("quilt-only");
        Quilt(folder, "quilt.jar", "quilt_only");
        L(436, Issues(Scan(folder), "LoaderMismatch").Length == 1 && Issues(Scan(folder, Context("quilt_loader", "0.27.0")), "LoaderMismatch").Length == 0);
        L(437, Issues(Scan(folder, Context("", "")), "LoaderMismatch").Length == 0);
        folder = f.Folder("forge-neo-platform");
        string platform = "[[mods]]\nmodId=\"platform_owner\"\nversion=\"1\"\n[[dependencies.platform_owner]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"";
        Jar(folder, "forge.jar", Entries(("META-INF/mods.toml", platform)));
        string oldNeo = platform.Replace("platform_owner", "neo_owner").Replace("modId=\"forge\"", "modId=\"neoforge\"").Replace("[47,)", "[20,)");
        Jar(folder, "old-neo.jar", Entries(("META-INF/mods.toml", oldNeo)));
        var neoContext = Context("neoforge", "20.4.200");
        neoContext.MinecraftVersion = "1.20.4";
        report = Scan(folder, neoContext);
        L(443, Issues(report, "LoaderMismatch", "platform_owner").Length == 1 && Issues(report, "LoaderMismatch", "neo_owner").Length == 0);
        report = Scan(folder, Context("forge", "47.2.0"));
        L(445, Issues(report, "LoaderMismatch", "neo_owner").Length == 1 && Issues(report, "LoaderMismatch", "platform_owner").Length == 0);
        folder = f.Folder("loader-list-trailing");
        foreign = Jar(folder, "forge.jar", Entries(("META-INF/mods.toml", platform)));
        Scan(folder + "\\");
        L(448, !string.IsNullOrEmpty(Warning(folder, foreign)));
        folder = f.Folder("fabric-schema-zero");
        Jar(folder, "old.jar", Entries(("fabric.mod.json", """{"id":"old_fabric","version":"1","side":"universal","requires":{"old_missing":[">=1 <2",">=3"]},"recommends":{"ignored_suggestion":"*"}}""")));
        Jar(folder, "server.jar", Entries(("fabric.mod.json", """{"schemaVersion":0,"id":"old_server","version":"1","side":"server","requires":{"server_missing":"*"}}""")));
        report = Scan(folder);
        L(454, Issues(report, "MissingRequired", "old_fabric").Length == 1 && Issues(report, "MissingRequired", "old_server").Length == 0 && !report.MetadataIncomplete);
        Fabric(folder, "old-target.jar", "old_missing", "1.5");
        report = Scan(folder);
        L(456, !report.HasErrors && Issues(report, "Recommendation").Length == 0);
        folder = f.Folder("forge-file-properties");
        Jar(folder, "properties.jar", Entries(("META-INF/mods.toml", "modLoader=\"javafml\"\nloaderVersion=\"[47,)\"\n[[mods]]\nmodId=\"props_library\"\nversion=\"${file.libraryVersion}\"\n[properties]\nlibraryVersion=\"1.2.3\"")));
        L(462, Scan(folder, Context("forge", "47.2.0")).Mods[0].Version == "1.2.3");
        folder = f.Folder("neo-metadata-generations");
        Jar(folder, "old-format.jar", Entries(("META-INF/mods.toml", oldNeo)));
        Jar(folder, "new-format.jar", Entries(("META-INF/neoforge.mods.toml", oldNeo)));
        report = Scan(folder, Context("neoforge", "21.1.100"));
        wrong = Issues(report, "LoaderMismatch");
        L(468, wrong.Length == 1 && Path.GetFileName(wrong[0].Owner.RootPath) == "old-format.jar");
        report = Scan(folder, neoContext);
        wrong = Issues(report, "LoaderMismatch");
        L(470, wrong.Length == 1 && Path.GetFileName(wrong[0].Owner.RootPath) == "new-format.jar");
        folder = f.Folder("neo-dual-generations");
        Jar(folder, "dual.jar", Entries(("META-INF/mods.toml", oldNeo), ("META-INF/neoforge.mods.toml", oldNeo)));
        L(472, !Scan(folder, neoContext).HasErrors && !Scan(folder, Context("neoforge", "21.1.100")).HasErrors);
        folder = f.Folder("three-loader-matrix");
        Fabric(folder, "fabric.jar", "fabric_matrix", "1", Obj("""{"depends":{"fabricloader":">=0.15"}}"""));
        Jar(folder, "forge.jar", Entries(("META-INF/mods.toml", platform.Replace("[47,)", "[52,)"))));
        Jar(folder, "neo.jar", Entries(("META-INF/neoforge.mods.toml", oldNeo)));
        foreach (string loader in new[]
        {
            "fabricloader",
            "forge",
            "neoforge"
        }

        )
        {
            var context = Context(loader, loader == "fabricloader" ? "0.16.10" : loader == "forge" ? "52.0.0" : "21.1.100");
            context.MinecraftVersion = "1.21.1";
            report = Scan(folder, context);
            L(481, Issues(report, "LoaderMismatch").Length == 2 && Issues(report, "MissingRequired").Length == 0 && Issues(report, "VersionMismatch").Length == 0, loader);
        }

        folder = f.Folder("legacy-annotations");
        var legacyContext = Context("forge", "14.23.5.2855");
        legacyContext.MinecraftVersion = "1.12.2";
        string modernTemplate = "modLoader=\"javafml\"\nloaderVersion=\"[31,)\"\n[[mods]]\nmodId=\"examplemod\"\nversion=\"1\"";
        Jar(folder, "scaling.jar", Entries(("mcmod.info", """[{"modid":"bountiful","version":"1","requiredMods":["MinecraftForge,missing_template"]}]"""), ("META-INF/mods.toml", modernTemplate), ("fixture/Mod.class", LegacyClass(("modid", "scalingwealth"), ("name", "Scaling Wealth"), ("version", "1.0.1"), ("dependencies", "required-after:forge@[14.21.1.2443,)"), ("acceptedMinecraftVersions", "[1.12]")))));
        Jar(folder, "bountiful.jar", Entries(("mcmod.info", """[{"modid":"bountiful","version":"2"}]"""), ("fixture/Mod.class", LegacyClass(("modid", "bountiful"), ("name", "Bountiful"), ("version", "2.2.2")))));
        report = Scan(folder, legacyContext);
        L(489, report.Mods.Count(m => m.Id == "scalingwealth") == 1 && report.Issues.Count == 0);
        Jar(folder, "annotation-only.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "bare_legacy"), ("version", "1")))));
        L(491, Issues(Scan(folder), "LoaderMismatch").Length == 3);
        byte[] scala = LegacyClass(("modid", "scala_fixture"), ("version", "1"));
        Jar(folder, "scala-mirror.jar", Entries(("fixture/Mod.class", scala), ("fixture/Mod$.class", scala), ("fixture/Mod 2.class", scala), ("__MACOSX/Mod.class", scala)));
        L(493, Issues(Scan(folder, legacyContext), "DuplicateId").Length == 0);
        Jar(folder, "requires.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "actual_owner"), ("version", "1"), ("dependencies", "required-client-after:actual_missing@[1,2);after:absent_optional@[2,);required-server:server_only"), ("acceptedMinecraftVersions", "[1.12.2]")))));
        report = Scan(folder, legacyContext);
        var missing = Issues(report, "MissingRequired", "actual_owner");
        L(496, missing.Length == 1 && missing[0].Requirement.Id == "actual_missing");
        Jar(folder, "duplicate.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "scalingwealth"), ("version", "1")))));
        L(498, Issues(Scan(folder, legacyContext), "DuplicateId").Length == 1);
        folder = f.Folder("legacy-contained");
        string embedded = f.Folder("legacy-embedded");
        string inner = Jar(embedded, "contained.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "legacy_inner"), ("version", "1.1")))));
        string outer = Jar(folder, "outer.jar", Entries(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nContainedDeps: contained.jar\r\n\r\n"), ("META-INF/libraries/contained.jar", File.ReadAllBytes(inner)), ("fixture/Mod.class", LegacyClass(("modid", "legacy_outer"), ("version", "1"), ("dependencies", "required-after:legacy_inner@[1,2)")))));
        report = Scan(folder, legacyContext);
        L(504, report.Mods.Count == 2 && report.Issues.Count == 0 && !report.MetadataIncomplete);
        report.Mods[0].Name = "mutated fixture";
        var cached = Scan(folder, legacyContext);
        L(506, cached.Mods[0].Name != "mutated fixture" && cached.Issues.Count == 0);
        File.Delete(outer);
        Jar(folder, "outer.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "legacy_outer"), ("version", "2"), ("dependencies", "required-after:truly_missing")))));
        L(508, Issues(Scan(folder, legacyContext), "MissingRequired").Length == 1);
        folder = f.Folder("legacy");
        Jar(folder, "legacy.jar", Entries(("mcmod.info", """[{"modid":"legacy","name":"Legacy","version":"1.0","requiredMods":["old_library@[1,2)"]}]""")));
        report = Scan(folder, Context("forge", "14.23"));
        L(513, report.Mods.Count == 1 && report.MetadataIncomplete && !report.HasErrors);
        Jar(Path.Combine(folder, "1.12.2"), "legacy-folder.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "version_folder"), ("version", "1")))));
        var withFolder = Context("forge", "14.23");
        withFolder.LegacyForgeVersionFolder = "1.12.2";
        L(517, Scan(folder, withFolder).Mods.Count == 2);
    }

    private static void LocalInstanceContext()
    {
        // Unlike the PS standalone branch, these are the genuine Main instance and McVersion.
        Type instanceType = Native.Type("PCL.ModMinecraft+McInstance");
        object instance = RuntimeHelpers.GetUninitializedObject(instanceType);
        var version = new McVersion("1.20.1");
        version.ModLoaderVersions.Add(ModLoaders.Fabric, "0.27.1");
        Native.Property(instanceType, "Version").SetValue(instance, version);
        Native.Property(instanceType, "JsonObject").SetValue(instance, Obj("""{"libraries":[{"name":"org.quiltmc:quilt-loader:0.27.1"}]}"""));
        TContext context = (TContext)Native.Call(Inspector, "ContextForInstance", null, instance, "1.8.0_402")!;
        L(525, context.LoaderKnown && context.LoaderVersions["quilt_loader"] == "0.27.1" && context.LoaderVersions.ContainsKey("fabricloader") && string.IsNullOrEmpty(context.LoaderVersions["fabricloader"]));
        version.ModLoaderVersions.Clear();
        version.ModLoaderVersions.Add(ModLoaders.Forge, "14.23.5");
        version.VanillaName = "1.12.2";
        Native.Property(instanceType, "JsonObject").SetValue(instance, new JObject());
        context = (TContext)Native.Call(Inspector, "ContextForInstance", null, instance, "17.0.1")!;
        L(528, context.LoaderVersions["forge"] == "14.23.5" && context.LegacyForgeVersionFolder == "1.12.2");
        version.ModLoaderVersions.Clear();
        version.ModLoaderVersions.Add(ModLoaders.NeoForge, "21.1.100");
        version.VanillaName = "1.21.1";
        Native.Property(instanceType, "JsonObject").SetValue(instance, Obj("""{"libraries":[{"name":"net.neoforged.fancymodloader:loader:4.0.42"}]}"""));
        context = (TContext)Native.Call(Inspector, "ContextForInstance", null, instance, "21.0.1")!;
        L(531, context.AdditionalBuiltins["javafml"] == "4.0.42");
        version.Reliable = false;
        context = (TContext)Native.Call(Inspector, "ContextForInstance", null, instance, null!)!;
        L(534, string.IsNullOrEmpty(context.MinecraftVersion));
    }

    private static void LocalIndex(Scope f, FixtureEnvironment environment)
    {
        string folder = f.Folder("persistent-tree");
        var context = Context();
        string core = Fabric(folder, "cached-core.jar", "cached_core", "1.0");
        string consumer = Fabric(folder, "cached-user.jar", "cached_user", "1.0", Obj("""{"depends":{"cached_core":">=1","java":">=21"}}"""));
        var first = Scan(folder, context);
        L(542, !first.IndexHit && first.IndexPersisted);
        string indexPath = (string)Native.Call(Index, "IndexPath", null, folder, context)!;
        var newRuntime = Context();
        newRuntime.JavaVersion = "21.0.2";
        using (var lease = new FileStream(consumer, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ClearIndex();
            var second = Scan(folder, context);
            L(548, second.IndexHit && second.Mods.Count == 2);
            var edge = second.Edges.First(e => e.Requirement.Id == "cached_core");
            L(550, ReferenceEquals(edge.Owner, second.Mods.Single(m => m.Id == "cached_user")) && ReferenceEquals(edge.Requirement, edge.Owner.Dependencies.Single(d => d.Id == "cached_core")));
            var third = Scan(folder, newRuntime);
            L(552, third.IndexHit && third.IndexReevaluated && !third.HasErrors);
            L(566, StartIndexChild(environment, folder, Path.Combine(f.Root, "dependency-index")) == 0);
        }

        first.Mods[0].Name = "mutated report";
        var fresh = Scan(folder, newRuntime);
        L(569, fresh.Mods[0].Name != "mutated report");
        File.Move(core, core + ".disabled");
        var disabled = Scan(folder, newRuntime);
        L(571, !disabled.IndexHit && Issues(disabled, "MissingRequired").Length == 1);
        File.Move(core + ".disabled", core);
        var enabled = Scan(folder, newRuntime);
        L(573, !enabled.IndexHit && !enabled.HasErrors);
        string added = Fabric(folder, "new-mod.jar", "new_mod");
        var expanded = Scan(folder, newRuntime);
        L(575, !expanded.IndexHit && expanded.Mods.Count == 3);
        File.Delete(added);
        enabled = Scan(folder, newRuntime);
        L(577, !enabled.IndexHit && enabled.Mods.Count == 2);
        string? generation = enabled.IndexGeneration;
        var forced = Rebuild(folder, newRuntime);
        L(580, !forced.IndexHit && forced.IndexGeneration != generation);
        File.WriteAllText(indexPath, "{broken snapshot");
        ClearIndex();
        L(582, !Scan(folder, newRuntime).IndexHit);
        var document = JObject.Parse(File.ReadAllText(indexPath));
        document["Tree"]!["Mods"] = JValue.CreateNull();
        File.WriteAllText(indexPath, document.ToString());
        ClearIndex();
        L(585, !Scan(folder, newRuntime).IndexHit);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            bool rejected = false;
            try
            {
                Scan(folder, newRuntime, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                rejected = true;
            }

            L(588, rejected);
        }

        string lockedFolder = f.Folder("first-index-locked"), lockedJar = Fabric(lockedFolder, "locked.jar", "locked_mod");
        using (var lease = new FileStream(lockedJar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = Scan(lockedFolder);
            L(591, partial.IndexReadFailed && !partial.IndexPersisted);
        }

        var recovered = Scan(lockedFolder);
        L(593, !recovered.IndexHit && recovered.IndexPersisted && recovered.Mods.Count == 1);
        string legacyFolder = f.Folder("manual-legacy-index"), packages = f.Folder("manual-legacy-packages");
        var legacyContext = Context("forge", "14.23.5.2855");
        legacyContext.MinecraftVersion = "1.12.2";
        string legacyJar = Jar(legacyFolder, "manual.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "manual_legacy"), ("version", "1")))), CompressionLevel.NoCompression);
        string replacement = Jar(packages, "replacement.jar", Entries(("fixture/Mod.class", LegacyClass(("modid", "manual_legacy"), ("version", "2")))), CompressionLevel.NoCompression);
        FileInfo original = new(legacyJar);
        DateTime written = original.LastWriteTimeUtc, created = original.CreationTimeUtc;
        L(598, Scan(legacyFolder, legacyContext).Mods[0].Version == "1");
        L(599, new FileInfo(replacement).Length == original.Length);
        File.WriteAllBytes(legacyJar, File.ReadAllBytes(replacement));
        File.SetCreationTimeUtc(legacyJar, created);
        File.SetLastWriteTimeUtc(legacyJar, written);
        L(602, Rebuild(legacyFolder, legacyContext).Mods[0].Version == "2");
        var cache = Native.Property(Index, "CacheDirectory");
        object? previous = cache.GetValue(null);
        string blocked = Path.Combine(f.Root, "cache-is-a-file");
        File.WriteAllText(blocked, "fixture");
        try
        {
            cache.SetValue(null, blocked);
            var memory = Scan(folder, newRuntime);
            L(607, !memory.IndexPersisted && !memory.HasErrors);
            using var lease = new FileStream(consumer, FileMode.Open, FileAccess.Read, FileShare.None);
            memory = Scan(folder, newRuntime);
            L(609, memory.IndexHit && !memory.IndexPersisted);
        }
        finally
        {
            cache.SetValue(null, previous);
        }
    }

    private static int StartIndexChild(FixtureEnvironment environment, string mods, string cache)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Real Native facade apphost missing", executable);
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = environment.Root,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string value in new[]
        {
            "--isolated",
            "--fixture",
            environment.Root,
            "--dependency-index-child",
            "--dependency-mods",
            mods,
            "--dependency-cache",
            cache
        }

        )
            start.ArgumentList.Add(value);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start actual native index child");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            process.Kill(true);
            process.WaitForExit(5000);
            throw new TimeoutException("Actual dependency-index child timed out");
        }

        Console.Write(output.GetAwaiter().GetResult());
        Console.Error.Write(error.GetAwaiter().GetResult());
        return process.ExitCode;
    }

    private static int IndexChild(FixtureEnvironment environment, string[] args)
    {
        string Read(string key)
        {
            int i = Array.IndexOf(args, key);
            return i < 0 || i + 1 == args.Length ? throw new ArgumentException("Missing index child " + key) : Path.GetFullPath(args[i + 1]);
        }

        string mods = Read("--dependency-mods"), cache = Read("--dependency-cache");
        foreach (string path in new[]
        {
            mods,
            cache
        }

        )
            Native.Check(path.StartsWith(Native.Full(environment.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Index child path escaped its GUID fixture");
        var property = Native.Property(Index, "CacheDirectory");
        object? previous = property.GetValue(null);
        try
        {
            property.SetValue(null, cache);
            var context = Context();
            context.JavaVersion = "21.0.2";
            var report = Scan(mods, context);
            Native.Check(report.IndexHit && !report.HasErrors && report.Mods.Count == 2, "Restart did not restore the genuine Main persistent tree while its JAR is exclusively held");
            Console.WriteLine("PASS fresh native process reused disk dependency tree with parent-held exclusive JAR");
            environment.AssertNoForbiddenIo();
            return 0;
        }
        finally
        {
            ClearIndex();
            property.SetValue(null, previous);
        }
    }
}
