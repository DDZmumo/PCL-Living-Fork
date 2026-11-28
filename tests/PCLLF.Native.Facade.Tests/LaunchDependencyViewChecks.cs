using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;
using PCLLF.Wpf.Glass;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static FrameworkElement LaunchCheckView(TReport report, object? plan)
        => (FrameworkElement)new GlassModDependencyCheckView("启动前模组检查", (ModDependencyInspector.InspectionReport)(object)report, (ModDependencyAutoFill.Plan?)plan);

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
        LaunchForbiddenRangeView(f);
        FrameworkElement blocked = LaunchCheckView(PlanReport(unavailable), unavailable);
        LD(158, LaunchViewText(blocked).Any(text => text.StartsWith("暂不能自动补全", StringComparison.Ordinal)));

        using (var launcher = new GlassLauncherView())
        {
            Native.Cache("LaunchModDependencyCheck", true); Native.Cache("LaunchModDependencyAutoFill", true);
            ShowDependencyOptions(launcher);
            var expand = LaunchPart<Button>(launcher, "ExpandButton");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, expand));
            var panel = LaunchPart<FrameworkElement>(launcher, "AuxiliaryCards");
            var master = LaunchPart<CheckBox>(launcher, "DependencyCheckBox");
            var auto = LaunchPart<CheckBox>(launcher, "AutoFillCheckBox");
            LD(164, LaunchPart<FrameworkElement>(launcher, "RecentWorldCard").Visibility == Visibility.Collapsed &&
                panel.Visibility == Visibility.Visible && panel.IsHitTestVisible);
            LD(165, master.IsChecked == true && auto.IsChecked == true && auto.Visibility == Visibility.Visible);
            Native.Cache("LaunchModDependencyCheck", false);
            ShowDependencyOptions(launcher); LaunchDrainUi();
            LD(170, auto.Visibility == Visibility.Collapsed);
        }
        Native.Static("PCL.ModLaunch", "McLaunchDependencyCheck", (object?)null);
        string ownerRoot = Path.Combine(f.Root, "versions", "fixture");
        Directory.CreateDirectory(ownerRoot);
        var owner = new ModMinecraft.McInstance(ownerRoot);
        Native.Check(owner.Config.ContextStamp.Kind == PCLCS.MmcConfigurationContextKind.Unmanaged,
            "The dependency view fixture did not attach an established ordinary instance root.");
        object emptyPlan = LaunchNew("PCL.ModDependencyAutoFill+Plan");
        Native.Set(emptyPlan, "Root", Path.Combine(owner.PathIndie, "mods"));
        Native.Static("PCL.ModLaunch", "InstallLaunchDependencies", owner, emptyPlan, CancellationToken.None);
        LD(173, true);
        LD(174, !ModMinecraft.McInstanceLock.IsLocked(owner));
        Native.Cache("LaunchModDependencyCheck", true);

        object plan = f.Plan();
        for (int i = 0; i < 7; i++)
            LaunchFabric(Path.Combine(f.Mods, "example" + i + ".jar"), "example" + i, new JObject { ["minecraft"] = "<1.21" });
        TReport report = f.Scan();
        for (int i = 0; i < 79; i++) report.Notices.Add("Synthetic metadata reading note " + i);
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
        Native.Check(LaunchViewText(LaunchCheckView(comparison, null)).Any(text => text.StartsWith("平台声明与本地状态", StringComparison.Ordinal)),
            "The native view omitted platform-only review details.");

        FrameworkElement view = LaunchCheckView(report, plan);
        Expander details = LaunchElements(view).OfType<Expander>()
            .Single(item => item.Header is TextBlock text && text.Text.StartsWith("检查说明", StringComparison.Ordinal));
        LD(179, !details.IsExpanded);
        using (var modal = Native.Dialog(new ModMain.MyMsgBoxConverter {
            Title = "启动前模组检查", Text = "请检查以下模组问题。", Button1 = "补全并启动", Button2 = "取消启动",
            Button3 = "仍然启动", IsWarn = true, WarningButton = 3, ContentFactory = () => view
        })) {
            var buttons = (Button[])Native.Get(modal, "_buttons")!;
            var danger = (Brush)modal.FindResource("GlassPresentationDanger");
            LD(185, !Equals(buttons[0].Foreground, danger) && Equals(buttons[2].Foreground, danger));
        }

        string foreign = Path.Combine(f.Root, "foreign-mods"); Directory.CreateDirectory(foreign);
        foreach (string loader in new[] { "forge", "neoforge" })
            Jar(foreign, loader + "-only.jar", Entries((loader == "forge" ? "META-INF/mods.toml" : "META-INF/neoforge.mods.toml",
                "[[mods]]\nmodId=\"" + loader + "_fixture\"\nversion=\"1.0\"\ndisplayName=\"" + loader + " fixture\"")));
        TReport foreignReport = Scan(foreign, f.Context);
        LD(194, foreignReport.Issues.Count(issue => Native.Get(issue, "Kind")!.ToString() == "LoaderMismatch") == 2);
        int requests = f.Requests.Count;
        object foreignPlan = LaunchPlan(foreign, foreignReport, f.Options());
        LD(196, PlanList(foreignPlan, "Items").Count == 0 && PlanList(foreignPlan, "Enables").Count == 0 && f.Requests.Count == requests);
        LD(198, LaunchViewText(LaunchCheckView(foreignReport, foreignPlan)).Any(text => text.StartsWith("加载器不匹配", StringComparison.Ordinal)));

        string runtimeFolder = Path.Combine(f.Root, "runtime-components"); Directory.CreateDirectory(runtimeFolder);
        foreach (string id in new[] { "runtime_one", "runtime_two" })
            Jar(runtimeFolder, id + ".jar", Entries(("META-INF/mods.toml",
                "modLoader=\"javafml\"\nloaderVersion=\"[48,)\"\n[[mods]]\nmodId=\"" + id + "\"\nversion=\"1\"\n[[dependencies." + id + "]]\nmodId=\"forge\"\nmandatory=true\nversionRange=\"[47,)\"\n[[dependencies." + id + "]]\nmodId=\"lowcodefml\"\nmandatory=true\nversionRange=\"[48,)\"")));
        TContext runtimeContext = new() { MinecraftVersion = "1.20.1", LoaderKnown = true };
        runtimeContext.LoaderVersions.Add("forge", "47.2.0");
        TReport runtimeReport = Scan(runtimeFolder, runtimeContext);
        requests = f.Requests.Count;
        object runtimePlan = LaunchPlan(runtimeFolder, runtimeReport, f.Options());
        LD(214, PlanList(runtimePlan, "Items").Count == 0 && f.Requests.Count == requests);
        string[] groups = LaunchViewText(LaunchCheckView(runtimeReport, runtimePlan))
            .Where(text => text.StartsWith("运行环境要求不满足", StringComparison.Ordinal)).ToArray();
        LD(217, groups.Length == 1 && groups[0].Contains("（1）", StringComparison.Ordinal));
        LD(218, !runtimeReport.ToSummary().Contains("javafml", StringComparison.Ordinal) && !runtimeReport.ToSummary().Contains("lowcodefml", StringComparison.Ordinal));
        var graph = (FrameworkElement)LaunchConstruct("PCL.ModDependencyGraphView", runtimeReport, Path.Combine(runtimeFolder, "runtime_one.jar"));
        var canvas = (Canvas)Native.Get(graph, "GraphCanvas")!;
        string?[] titles = canvas.Children.OfType<Border>().Where(border => border.Tag is not null)
            .Select(border => (string?)Native.Get(border.Tag, "Title")).ToArray();
        LD(221, titles.Count(title => title == "Forge") == 1 && !titles.Any(title => title is "javafml" or "lowcodefml"));

        var modPage = new PageInstanceMod();
        LD(226, Equals(LaunchPart<Button>(modPage, "BtnManageIndex").Content, "更新依赖索引"));
        var fabricContext = (ModDependencyInspector.InspectionContext)(object)f.Context;
        var wrongRows = new List<GlassLocalModRow>();
        GlassLocalModRow CreateRow(string path) => new(modPage, new LocalResourceFile(path), false);
        bool Warns(GlassLocalModRow row) => LaunchViewText((Panel)Native.Get(row, "_chips")!)
            .Any(text => text.Contains("Fabric", StringComparison.Ordinal));
        void RefreshWarning(GlassLocalModRow row, ModDependencyInspector.InspectionContext context) {
            row.RefreshLoaderWarning(foreign, context);
            row.Refresh();
        }
        foreach (string file in Directory.GetFiles(foreign, "*.jar")) {
            var row = CreateRow(file);
            RefreshWarning(row, fabricContext);
            LD(234, Warns(row));
            var button = (Button)Native.Get(row, "_body")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            LD(236, row.Checked && Warns(row));
            wrongRows.Add(row);
        }
        string normal = LaunchFabric(Path.Combine(foreign, "fabric-correct.jar"), "correct_loader");
        Scan(foreign, f.Context);
        var correct = CreateRow(normal);
        RefreshWarning(correct, fabricContext);
        LD(241, !Warns(correct));
        var wrong = wrongRows[0];
        var changedContext = new ModDependencyInspector.InspectionContext { MinecraftVersion = "1.21.1", LoaderKnown = true };
        changedContext.LoaderVersions.Add("neoforge", "21.1.100");
        RefreshWarning(wrong, changedContext);
        LD(245, !Warns(wrong));
        string enabledPath = wrong.Entry.File.FullName, disabledPath = enabledPath + ".disabled";
        File.Move(enabledPath, disabledPath);
        var disabled = CreateRow(disabledPath);
        RefreshWarning(disabled, fabricContext);
        LD(247, !Warns(disabled));

        TContext legacyContext = new() { MinecraftVersion = "1.12.2", LoaderKnown = true };
        legacyContext.LoaderVersions.Add("forge", "14.23.5.2855");
        TReport empty = Scan(Path.Combine(f.Root, "empty-legacy"), legacyContext);
        TMod description = new() { Id = "legacy_fixture", Enabled = true, RootPath = Path.Combine(f.Root, "legacy-fixture.jar") };
        empty.Mods.Add(description);
        requests = f.Requests.Count;
        object legacyPlan = LaunchPlan(foreign, empty, f.Options());
        LD(253, PlanList(legacyPlan, "Items").Count == 0 && f.Requests.Count == requests);
        TIssue uncertain = new() { Owner = description, Requirement = new TDependency { Id = "runtime_unconfirmed" } };
        FieldInfoSetEnum(uncertain, "Kind", "MissingRequired");
        empty.MetadataIncomplete = true; empty.Issues.Add(uncertain);
        legacyPlan = LaunchPlan(foreign, empty, f.Options());
        LD(256, PlanList(legacyPlan, "Items").Count == 0 && f.Requests.Count == requests &&
            PlanNotes(legacyPlan).Any(note => note.Contains("运行时声明尚未确认", StringComparison.Ordinal)));
        foreach (string key in new[] { "LaunchModDependencyCheck", "LaunchModDependencyAutoFill" })
            LD(257, Settings.Entries[key].Source.ToString() == "Normal");
        Console.WriteLine("PASS launch original 158..257: native glass modal, dependency graph, workspace, warning chips and actual runtime/lease guards.");
    }

    private static void FieldInfoSetEnum(object instance, string name, string value)
    {
        System.Reflection.FieldInfo field = Native.Field(instance.GetType(), name); field.SetValue(instance, Enum.Parse(field.FieldType, value));
    }

    private static IEnumerable<DependencyObject> LaunchElements(DependencyObject root)
    {
        yield return root;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (DependencyObject element in LaunchElements(child)) yield return element;
    }

    private static IEnumerable<string> LaunchViewText(DependencyObject root) =>
        LaunchElements(root).OfType<TextBlock>().Select(label => label.Text);
}
