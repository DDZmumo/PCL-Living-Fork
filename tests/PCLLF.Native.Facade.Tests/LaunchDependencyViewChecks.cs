using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static FrameworkElement LaunchCheckView(TReport report, object? plan)
        => (FrameworkElement)LaunchConstruct("PCL.ModDependencyCheckView", report, plan);
    private static Panel LaunchGroups(FrameworkElement view) => (Panel)Native.Get(view, "Groups")!;

    private static void LaunchForbiddenRangeView(LaunchFixture f)
    {
        foreach(string version in new[]{"1.10.7+mc1.21.11","1.10.8+mc1.21.11"}) {
            string folder=Path.Combine(f.Root,"forbidden-range-"+version); Directory.CreateDirectory(folder);
            Jar(folder,"consumer.jar",Entries(("fabric.mod.json",new JObject { ["schemaVersion"]=1,["id"]="range_consumer",["version"]="0.8.15-beta.1",
                ["breaks"]=new JObject { ["range_provider"]="<=1.10.7" } }.ToString())));
            Jar(folder,"provider.jar",Entries(("fabric.mod.json",new JObject { ["schemaVersion"]=1,["id"]="range_provider",["version"]=version }.ToString())));
            var report=Scan(folder,f.Context);
            bool blocked=version.StartsWith("1.10.7+",StringComparison.Ordinal);
            Native.Check(report.HasErrors==blocked,"A Fabric build suffix changed whether the declared forbidden version range matched.");
            if(!blocked) continue;
            var view=LaunchCheckView(report,null);
            IEnumerable<DependencyObject> Walk(DependencyObject root) {
                yield return root;
                foreach(object child in LogicalTreeHelper.GetChildren(root))
                    if(child is DependencyObject element) foreach(var nested in Walk(element)) yield return nested;
            }
            Native.Check(Walk(view).OfType<TextBlock>().Any(text=>text.Text.Contains("禁止共存版本：range_provider <=1.10.7",StringComparison.Ordinal)&&text.Text.Contains(version,StringComparison.Ordinal)),
                "The actual launch conflict view did not identify its range as forbidden or lost the installed full version.");
        }
    }

    private static void LaunchViews(LaunchFixture f, object unavailable)
    {
        Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
        LaunchForbiddenRangeView(f);
        FrameworkElement blocked = LaunchCheckView(PlanReport(unavailable), unavailable);
        LD(158, LaunchGroups(blocked).Children.OfType<MyHint>().Any(hint => hint.Text.StartsWith("暂不能自动补全", StringComparison.Ordinal)));
        PageLaunchRight page = new(); LaunchPart<FrameworkElement>(page, "PanLog").Visibility = Visibility.Collapsed;
        Native.Cache("LaunchModDependencyCheck", true); Native.Cache("LaunchModDependencyAutoFill", true);
        Native.Call(page.GetType(), "RefreshDependencyOptions", page);
        Native.Call(page.GetType(), "ApplyDependencyOptionsState", page, false, false, true);
        Native.Call(page.GetType(), "ShowRecentWorld", page, (object?)null);
        LD(164, LaunchPart<FrameworkElement>(page, "CardDependencyChecks").Visibility == Visibility.Visible);
        LD(165, LaunchPart<FrameworkElement>(page, "CheckAutoFillDependencies").Visibility == Visibility.Visible && LaunchPart<MyCheckBox>(page, "CheckModDependencies").Checked);
        FrameworkElement recent = LaunchPart<FrameworkElement>(page, "CardRecentWorlds"); recent.Visibility = Visibility.Visible; recent.Opacity = 1;
        LaunchPart<TranslateTransform>(page, "TransRecentWorld").X = 0;
        MyListItem recentItem = LaunchPart<MyListItem>(page, "ItemRecentWorld"); recentItem.Title = "新的世界"; recentItem.Info = "1.21.1 · 测试版本";
        Native.Cache("LaunchModDependencyCheck", false); Native.Call(page.GetType(), "RefreshDependencyOptions", page);
        LD(170, LaunchPart<FrameworkElement>(page, "CheckAutoFillDependencies").Visibility == Visibility.Collapsed);
        Native.Static("PCL.ModLaunch", "McLaunchDependencyCheck", (object?)null); // Disabled check must not dereference loader/authentication.
        string ownerRoot = Path.Combine(f.Root, "versions", "fixture");
        // Instance construction attaches its configuration provider immediately. Create the
        // owned ordinary directory first; a missing root correctly attaches as Unresolved.
        Directory.CreateDirectory(ownerRoot);
        ModMinecraft.McInstance owner = new(ownerRoot);
        Native.Check(owner.Config.ContextStamp.Kind == PCLCS.MmcConfigurationContextKind.Unmanaged,
            "The dependency view fixture did not attach an established ordinary instance root.");
        object emptyPlan = LaunchNew("PCL.ModDependencyAutoFill+Plan"); Native.Set(emptyPlan, "Root", Path.Combine(owner.PathIndie, "mods"));
        Native.Static("PCL.ModLaunch", "InstallLaunchDependencies", owner, emptyPlan, CancellationToken.None); LD(173, true);
        LD(174, !ModMinecraft.McInstanceLock.IsLocked(owner)); Native.Cache("LaunchModDependencyCheck", true);
        object plan = f.Plan();
        for (int i = 0; i < 7; i++) LaunchFabric(Path.Combine(f.Mods, "example" + i + ".jar"), "example" + i, new JObject { ["minecraft"] = "<1.21" });
        TReport report = f.Scan(); for (int i = 0; i < 79; i++) report.Notices.Add("Synthetic metadata reading note " + i);
        TReport comparison = new() { Context = report.Context, OnlineAttempted = true };
        TOnlineDependency required = new() { Platform = "Modrinth", ProjectId = "missing-platform", VersionId = "v1", Kind = "required" };
        TOnlineProject exact = new() { Platform = "Modrinth", ProjectId = "owner", VersionId = "owner-version", IsExact = true };
        exact.Dependencies.Add(required);
        TOnlineFile online = new() { FullPath = Path.Combine(f.Mods, "platform-owner.jar"), Enabled = true, IsSupplementTarget = true };
        online.Matches.Add(exact); comparison.OnlineFiles.Add(online);
        var port = (PCLCS.LegacyLaunchDependencyReport)Native.Static("PCL.ModLaunch", "DependencyReport", f.Mods, comparison)!;
        Native.Check(port.NeedsAttention && !comparison.HasErrors, "Platform-only review was lost or became a loader error.");
        Native.Call(LaunchSession, "Accept", null, f.Mods, comparison);
        Native.Check(LaunchAccepted(f.Mods, comparison), "Platform review acknowledgement was not remembered.");
        required.VersionId = "v2";
        Native.Check(!LaunchAccepted(f.Mods, comparison), "An earlier acknowledgement hid a changed platform version requirement.");
        var platformView = LaunchCheckView(comparison, null);
        Native.Check(LaunchGroups(platformView).Children.OfType<MyCard>().Any(card => card.Title.StartsWith("平台声明与本地状态", StringComparison.Ordinal)), "The native view omitted platform-only review details.");
        FrameworkElement view = LaunchCheckView(report, plan);
        LD(179, ((MyCard)Native.Get(view, "Details")!).IsSwapped);
        // This original optional-preview assertion is now unconditional: inspect
        // the actual Main modal/BAML without Show or launcher event-loop entry.
        ModMain.MyMsgBoxConverter converter = new()
        {
            Title = "启动前模组检查", Text = "请检查以下模组问题。", Button1 = "补全并启动", Button2 = "取消启动", Button3 = "仍然启动",
            IsWarn = true, WarningButton = 3, ContentFactory = () => view
        };
        MyMsgText modal = new(converter);
        LaunchPart<RotateTransform>(modal, "TransformRotate").Angle = 0; LaunchPart<TranslateTransform>(modal, "TransformPos").Y = 0;
        LD(185, LaunchPart<MyButton>(modal, "Btn1").ColorType.ToString() != "Red" && LaunchPart<MyButton>(modal, "Btn3").ColorType.ToString() == "Red");

        string foreign = Path.Combine(f.Root, "foreign-mods"); Directory.CreateDirectory(foreign);
        foreach (string loader in new[] { "forge", "neoforge" })
            Jar(foreign, loader + "-only.jar", Entries((loader == "forge" ? "META-INF/mods.toml" : "META-INF/neoforge.mods.toml",
                "[[mods]]\nmodId=\"" + loader + "_fixture\"\nversion=\"1.0\"\ndisplayName=\"" + loader + " fixture\"")));
        TReport foreignReport = Scan(foreign, f.Context);
        LD(194, foreignReport.Issues.Count(issue => Native.Get(issue, "Kind")!.ToString() == "LoaderMismatch") == 2);
        int requests = f.Requests.Count; object foreignPlan = LaunchPlan(foreign, foreignReport, f.Options());
        LD(196, PlanList(foreignPlan, "Items").Count == 0 && PlanList(foreignPlan, "Enables").Count == 0 && f.Requests.Count == requests);
        FrameworkElement foreignView = LaunchCheckView(foreignReport, foreignPlan);
        LD(198, LaunchGroups(foreignView).Children.OfType<MyCard>().Any(card => card.Title.StartsWith("加载器不匹配", StringComparison.Ordinal)));

        string runtime = Path.Combine(f.Root, "runtime-components"); Directory.CreateDirectory(runtime);
        foreach (string id in new[] { "runtime_one", "runtime_two" })
            Jar(runtime, id + ".jar", Entries(("META-INF/mods.toml", "modLoader=\"javafml\"\nloaderVersion=\"[48,)\"\n[[mods]]\nmodId=\"" + id + "\"\nversion=\"1\"\n[[dependencies." + id + "]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"\n[[dependencies." + id + "]]\nmodId=\"lowcodefml\"\nmandatory=true\nversionRange=\"[48,)\"")));
        TContext runtimeContext = new() { MinecraftVersion = "1.20.1", LoaderKnown = true }; runtimeContext.LoaderVersions.Add("forge", "47.2.0");
        TReport runtimeReport = Scan(runtime, runtimeContext); requests = f.Requests.Count;
        object runtimePlan = LaunchPlan(runtime, runtimeReport, f.Options()); LD(214, PlanList(runtimePlan, "Items").Count == 0 && f.Requests.Count == requests);
        FrameworkElement runtimeView = LaunchCheckView(runtimeReport, runtimePlan);
        MyCard[] cards = LaunchGroups(runtimeView).Children.OfType<MyCard>().Where(card => card.Title.StartsWith("运行环境要求不满足", StringComparison.Ordinal)).ToArray();
        LD(217, cards.Length == 1 && cards[0].Title.Contains("（1）", StringComparison.Ordinal));
        LD(218, !runtimeReport.ToSummary().Contains("javafml", StringComparison.Ordinal) && !runtimeReport.ToSummary().Contains("lowcodefml", StringComparison.Ordinal));
        FrameworkElement graph = (FrameworkElement)LaunchConstruct("PCL.ModDependencyGraphView", runtimeReport, Path.Combine(runtime, "runtime_one.jar"));
        Canvas canvas = (Canvas)Native.Get(graph, "GraphCanvas")!;
        string?[] titles = canvas.Children.OfType<Border>().Where(border => border.Tag is not null).Select(border => (string?)Native.Get(border.Tag, "Title")).ToArray();
        LD(221, titles.Count(title => title == "Forge") == 1 && !titles.Any(title => title is "javafml" or "lowcodefml"));

        PageInstanceMod modPage = new();
        MyButton index = LaunchPart<MyButton>(modPage, "BtnManageIndex"); LD(226, index.Text == "更新依赖索引");
        StackPanel rows = new();
        foreach (string file in Directory.GetFiles(foreign, "*.jar"))
        {
            MyLocalModItem row = new() { Entry = new LocalResourceFile(file), Title = Path.GetFileNameWithoutExtension(file), Description = Path.GetFileName(file) };
            Native.Call(row.GetType(), "RefreshLoaderWarning", row, foreign, f.Context);
            FrameworkElement warning = LaunchPart<FrameworkElement>(row, "LoaderWarningBorder");
            LD(234, warning.Visibility == Visibility.Visible && !warning.IsHitTestVisible && row.ToolTip.ToString()!.Contains("Fabric", StringComparison.Ordinal));
            row.Checked = true; row.Checked = false; LD(236, warning.Visibility == Visibility.Visible); rows.Children.Add(row);
        }
        string normal = LaunchFabric(Path.Combine(foreign, "fabric-correct.jar"), "correct_loader"); Scan(foreign, f.Context);
        MyLocalModItem correct = new() { Entry = new LocalResourceFile(normal), Title = "Fabric compatible", Description = "fabric-correct.jar" };
        Native.Call(correct.GetType(), "RefreshLoaderWarning", correct, foreign, f.Context);
        LD(241, LaunchPart<FrameworkElement>(correct, "LoaderWarningBorder").Visibility == Visibility.Collapsed); rows.Children.Add(correct);
        MyLocalModItem wrong = (MyLocalModItem)rows.Children[0];
        TContext changedContext = new() { MinecraftVersion = "1.21.1", LoaderKnown = true }; changedContext.LoaderVersions.Add("neoforge", "21.1.100");
        Native.Call(wrong.GetType(), "RefreshLoaderWarning", wrong, foreign, changedContext);
        LD(245, LaunchPart<FrameworkElement>(wrong, "LoaderWarningBorder").Visibility == Visibility.Collapsed);
        string enabledPath = wrong.Entry.File.FullName, disabledPath = enabledPath + ".disabled"; File.Move(enabledPath, disabledPath); wrong.Entry = new LocalResourceFile(disabledPath);
        Native.Call(wrong.GetType(), "RefreshLoaderWarning", wrong, foreign, f.Context);
        LD(247, LaunchPart<FrameworkElement>(wrong, "LoaderWarningBorder").Visibility == Visibility.Collapsed);

        TContext legacyContext = new() { MinecraftVersion = "1.12.2", LoaderKnown = true }; legacyContext.LoaderVersions.Add("forge", "14.23.5.2855");
        TReport empty = Scan(Path.Combine(f.Root, "empty-legacy"), legacyContext);
        TMod description = new() { Id = "legacy_fixture", Enabled = true, RootPath = Path.Combine(f.Root, "legacy-fixture.jar") }; empty.Mods.Add(description);
        requests = f.Requests.Count; object legacyPlan = LaunchPlan(foreign, empty, f.Options());
        LD(253, PlanList(legacyPlan, "Items").Count == 0 && f.Requests.Count == requests);
        TIssue uncertain = new() { Owner = description, Requirement = new TDependency { Id = "runtime_unconfirmed" } };
        FieldInfoSetEnum(uncertain, "Kind", "MissingRequired"); empty.MetadataIncomplete = true; empty.Issues.Add(uncertain);
        legacyPlan = LaunchPlan(foreign, empty, f.Options());
        LD(256, PlanList(legacyPlan, "Items").Count == 0 && f.Requests.Count == requests && PlanNotes(legacyPlan).Any(note => note.Contains("运行时声明尚未确认", StringComparison.Ordinal)));
        foreach (string key in new[] { "LaunchModDependencyCheck", "LaunchModDependencyAutoFill" }) LD(257, Settings.Entries[key].Source.ToString() == "Normal");
        Console.WriteLine("PASS launch original 158..257: real Main check/graph/modal/homepage/mod-row BAML, grouped runtime, warning clearing, disabled launch bypass, real completed instance worker lease and no speculative legacy downloads.");
    }

    private static void FieldInfoSetEnum(object instance, string name, string value)
    {
        System.Reflection.FieldInfo field = Native.Field(instance.GetType(), name); field.SetValue(instance, Enum.Parse(field.FieldType, value));
    }
}
