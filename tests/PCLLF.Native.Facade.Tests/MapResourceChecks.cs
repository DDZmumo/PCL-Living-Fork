using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;
// These are the original Main page/model hooks, not a second filter/navigation implementation.
internal static class MapResourceChecks
{
    internal static void MapDetails(FixtureEnvironment fixture)
    {
        object project = Project(ResourceTypes.Map, "map");
        Type version = Native.Type("PCL.ResourceVersion");
        IList items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(version))!;
        foreach (string game in new[]
        {
            "1.20.2",
            "1.20.1"
        }

        )
        {
            object item = Activator.CreateInstance(version)!;
            Native.Set(item, "ResourceType", ResourceTypes.Map);
            Native.Set(item, "Id", game);
            Native.Set(item, "FileName", "map.zip");
            Native.Set(item, "Display", "Fixture " + game);
            Native.Set(item, "ReleaseDate", DateTime.UtcNow.AddDays(-1));
            Native.Property(version, "ReleaseTypeCode").SetValue(item, 1);
            Native.Set(item, "GameVersions", new List<string> { game });
            Native.Set(item, "DownloadUrls", new List<string> { "https://example.invalid/native-facade/map.zip" });
            items.Add(item);
        }

        Type detail = Native.Type("PCL.PageDownloadResourceDetail");
        object page = RuntimeHelpers.GetUninitializedObject(detail);
        var field = Native.Field(detail, "ResourceFileLoader");
        object loader = RuntimeHelpers.GetUninitializedObject(field.FieldType);
        Native.Field(field.FieldType, "Output").SetValue(loader, items);
        field.SetValue(page, loader);
        var projectState = Native.Property(detail, "Project");
        var typeState = Native.Property(detail, "TargetResourceType");
        Native.Check(projectState.CanWrite && typeState.CanWrite, "Real detail state adapters are unavailable.");
        projectState.SetValue(page, project);
        foreach (ResourceTypes type in new[]
        {
            (ResourceTypes)0,
            ResourceTypes.Map,
            ResourceTypes.ResourcePack
        }

        )
        {
            typeState.SetValue(page, type);
            IList visible = (IList)Native.Call(detail, "GetResults", page)!;
            Native.Check(visible.Count == items.Count, "Main detail filter lost map files for " + type);
            for (int i = 0; i < items.Count; i++)
                Native.Check(ReferenceEquals(visible[i], items[i]), "Main detail filter copied live version models.");
        }

        object controller = Native.Field(detail, "Controller").GetValue(page)!;
        Native.Check(controller is not null && controller.GetType().Assembly.GetName().Name == "PCLCS" && controller.GetType().GetGenericArguments()[0] == project.GetType() && controller.GetType().GetGenericArguments()[1] == version, "Page adapter did not use the actual production controller and actual Main models.");
        Native.Check(ReferenceEquals(projectState.GetValue(page), project), "Detail adapter created a second project store.");
        DetailCardRendering(page, controller!, project, items);
    }

    private static void DetailCardRendering(object page, object controller, object project, IList versions)
    {
        object host = Native.Get(controller, "host")!;
        var setBuilder = (Action<PCL.MyCard, Action<System.Windows.Controls.StackPanel>>)Native.Get(host, "SetBuilder")!;
        var stack = new System.Windows.Controls.StackPanel();
        var card = new PCL.MyCard { SwapControl = stack };
        card.Children.Add(stack);
        int built = 0;
        setBuilder(card, actual => { Native.Check(ReferenceEquals(actual, stack), "Builder received a different panel."); built++; actual.Children.Add(new System.Windows.Controls.TextBlock { Text = "fixture" }); });
        card.BuildContent(); card.BuildContent();
        Native.Check(built == 1 && stack.Children.Count == 1, "Actual Main card builder failed or duplicated its content.");

        var view = new ResourceDetailPageView
        {
            Filter = new System.Windows.Controls.StackPanel(), Results = new System.Windows.Controls.StackPanel(), Intro = new System.Windows.Controls.StackPanel(),
            FilterCard = new System.Windows.Controls.Border(), Alternative = new System.Windows.Controls.TextBlock(), Wiki = new System.Windows.Controls.Button(),
            AlternativeText = _ => { }, WebText = _ => { }, ScrollHome = () => { }
        };
        // Replace only the detached page's viewport and navigation boundary.
        // Cards, lazy builders, sorting, version-row construction and filtering
        // continue through the genuine Main delegates and Core controller.
        Native.Set(host, "View", (Func<ResourceDetailPageView>)(() => view));
        Native.Set(host, "HasArguments", (Func<bool>)(() => false));
        foreach (ResourceTypes type in new[] { ResourceTypes.Mod, ResourceTypes.ModPack, ResourceTypes.Map, ResourceTypes.DataPack, ResourceTypes.ResourcePack, ResourceTypes.Shader })
        {
            Native.Set(project, "Types", type);
            Native.Set(controller, "TargetResourceType", type);
            Native.Set(controller, "VersionFilter", null);
            foreach (object version in versions) Native.Set(version, "ResourceType", type);
            Native.Call(controller.GetType(), "UpdateFilterResult", controller);
            Native.Check(view.Results.Children.Count == 2, "Download detail lost its version cards for " + type);
            foreach (PCL.MyCard result in view.Results.Children)
            {
                Native.Check(result.IsSwapped, "Multi-card results did not retain lazy collapsed state.");
                result.IsSwapped = false;
                var panel = (System.Windows.Controls.StackPanel)result.SwapControl!;
                Native.Check(panel.Tag is null && panel.Children.Count >= 2, "Expanding a real download card left its version list empty.");
                var rows = panel.Children.OfType<PCL.MyVirtualizingElement<PCL.MyListItem>>().ToList();
                Native.Check(rows.Count == 1, "Download card did not contain its actual version row.");
                foreach (var row in rows) Native.Check(row.Init().Tag is PCL.ResourceVersion, "Download version row did not materialize its genuine file model.");
                int count = panel.Children.Count;
                result.BuildContent();
                Native.Check(panel.Children.Count == count, "Rebuilding a download card duplicated versions.");
            }
            Native.Set(controller, "VersionFilter", "1.20.1");
            Native.Call(controller.GetType(), "UpdateFilterResult", controller);
            Native.Check(view.Results.Children.Count == 1, "Version filter did not select one download card.");
            var selected = (PCL.MyCard)view.Results.Children[0];
            Native.Check(!selected.IsSwapped && ((System.Windows.Controls.StackPanel)selected.SwapControl!).Children.Count >= 2, "Single filtered card did not build its versions automatically.");
            Native.Call(controller.GetType(), "LoadOnFinish", controller);
            Native.Check(view.Filter.Children.Count == 3 && view.Results.Children.Count == 2, "Actual filter-button initialization did not restore the complete version list.");
        }
        Console.WriteLine("PASS actual Main download detail: typed builder adaptation, six resource types, deferred expansion, single-card selection and filter-button rendering.");
    }

    internal static void ResourceEntries(FixtureEnvironment fixture)
    {
        DesktopCompatibilityChecks.SearchAndLegacyEncoding(fixture.Root);
        LocalResourceCacheCompatibilityChecks.Run(fixture.Root);
        LocalResourceUpdateDiscoveryChecks.Run(fixture.Root);
        Type module = Native.Type("PCL.ModMain");
        object queue = Native.Field(module, "WaitingMyMsgBox").GetValue(null)!;
        Native.Check((int)Native.Property(queue.GetType(), "Count").GetValue(queue)! == 0, "Unexpected existing dialog queue.");
        try
        {
            object result = Native.Call(module, "MyMsgBox", null, "fixture local-world details", "详情", "确定", null, null, false, true, false, null, null, null)!;
            Native.Check((int)result == 1, "Single-button Main details did not queue normally.");
            Array rows = (Array)Native.Call(queue.GetType(), "ToArray", queue)!;
            Native.Check(rows.Length == 1, "Single-button Main details changed queue count.");
            object queued = rows.GetValue(0)!;
            Native.Check((string)Native.Get(queued, "Button2")! == "" && (string)Native.Get(queued, "Button3")! == "", "Null captions were not normalized by actual Main.");
        }
        finally
        {
            Native.Call(queue.GetType(), "Clear", queue);
        }

        object project = Project(ResourceTypes.Shader, "shader");
        Type instanceType = Native.Type("PCL.ModMinecraft+McInstance");
        string instanceFolder = Path.Combine(fixture.Data, "versions", "example");
        Directory.CreateDirectory(instanceFolder);
        object instance = Instance(instanceType, instanceFolder);
        object download = Native.Enum("PCL.FormMain+PageType", "Download")!, detail = Native.Enum("PCL.FormMain+PageType", "ResourceDetail")!, instancePage = Native.Enum("PCL.FormMain+PageType", "InstanceSetup")!;
        object[] arguments = Build(project, instancePage, null, null, instance);
        Native.Check((string)arguments[2] == "1.20.1" && (ResourceTypes)arguments[4] == ResourceTypes.Shader && (ModLoaders)arguments[3] == ModLoaders.None, "Instance resource entry lost type/version or added a loader filter.");
        foreach (object[]? empty in new object[]? []
        {
            null,
            Array.Empty<object>(),
            new object[]
            {
                "incomplete"
            }
        }

        )
        {
            arguments = Build(project, detail, null, empty, null);
            Native.Check((ResourceTypes)arguments[4] == ResourceTypes.Any, "Incomplete dependency arguments changed the route.");
        }

        Type requestType = Native.Type("PCL.ResourceSearcher+SearchRequest");
        object searchResult = Activator.CreateInstance(Native.Type("PCL.ResourceSearcher+SearchResult"))!;
        object request = Activator.CreateInstance(requestType, ResourceTypes.Map, searchResult, 40)!;
        Native.Set(request, "GameVersion", "1.20.2");
        Native.Set(request, "ModLoaders", ModLoaders.Fabric);
        arguments = Build(project, download, request, null, null);
        Native.Check((ResourceTypes)arguments[4] == ResourceTypes.Map && (string)arguments[2] == "1.20.2" && (ModLoaders)arguments[3] == ModLoaders.None, "Download map entry lost search context.");
        arguments = Build(project, download, null, null, null);
        Native.Check((ResourceTypes)arguments[4] == ResourceTypes.Shader, "Missing search input did not use real project type.");
        Native.Check(arguments.Length == 6, "Main detail arguments lost the exact download target slot.");
        Native.Set(request, "Type", ResourceTypes.Mod);
        Native.Set(request, "GameVersion", "1.20.1");
        object[] bound = Build(project, download, request, null, instance);
        Native.Check(ReferenceEquals(bound[5], instance), "Version-bound search lost its exact instance.");
        object[] dependency = Build(project, detail, null, bound, null);
        Native.Check(ReferenceEquals(dependency[5], instance), "Dependency navigation lost its exact instance.");
        Native.Set(request, "GameVersion", "1.20.2");
        Native.Check(Build(project, download, request, null, instance)[5] is null, "Changed version filter retained an incompatible target.");
        DownloadFolderPriority(fixture, instanceType);
    }

    private static object Project(ResourceTypes type, string label)
    {
        string id = "NativeFacade" + label + Guid.NewGuid().ToString("N");
        JObject cache = new()
        {
            ["Tags"] = new JArray(),
            ["Platform"] = 1,
            ["Types"] = (int)type,
            ["Slug"] = "fixture-" + label,
            ["Id"] = id,
            ["RawName"] = "Fixture " + label,
            ["Description"] = "Fixture",
            ["Website"] = "https://example.invalid/fixture",
            ["LastUpdate"] = "2026-10-25T00:00:00Z",
            ["DownloadCount"] = 1
        };
        return Activator.CreateInstance(Native.Type("PCL.ResourceProject"), cache)!;
    }

    private static object Instance(Type type, string path)
    {
        object instance = type.GetConstructor([typeof(string)])!.Invoke([path]);
        Native.Property(type, "Version").SetValue(instance, new McVersion("1.20.1", true));
        return instance;
    }

    private static object[] Build(object project, object page, object? search, object[]? existing, object? instance) => (object[])Native.Static("PCL.MyResourceItem", "BuildDetailArguments", project, page, search, existing, instance)!;
    private static void DownloadFolderPriority(FixtureEnvironment fixture, Type instanceType)
    {
        object[] instances = new object[2];
        string[] names = ["selected", "other"];
        for (int i = 0; i < names.Length; i++)
        {
            string path = Path.Combine(fixture.Data, "downloads", "versions", names[i]);
            Directory.CreateDirectory(Path.Combine(path, "PCL"));
            File.WriteAllText(Path.Combine(path, "PCL", "Setup.ini"), "VersionArgumentIndieV2:True\r\n", new System.Text.UTF8Encoding(false));
            instances[i] = Instance(instanceType, path);
        }

        Type suitableType = typeof(Func<, >).MakeGenericType(instanceType, typeof(bool));
        ParameterExpression value = Expression.Parameter(instanceType, "value");
        Delegate suitable = Expression.Lambda(suitableType, Expression.Constant(true), value).Compile();
        Type detail = Native.Type("PCL.PageDownloadResourceDetail");
        string cached = Path.Combine(fixture.Data, "downloads", "minecraft");
        string Choose(object? target, object? selected) => (string)Native.Call(detail, "ChooseModDownloadFolder", null, target, selected, cached, suitable)!;
        string expected = Path.Combine(fixture.Data, "downloads", "versions", "selected", "mods");
        Native.Check(string.Equals(Native.Full(Choose(instances[0], instances[1])), Native.Full(expected), StringComparison.OrdinalIgnoreCase), "Cache overrode the explicit instance mods directory.");
        expected = Path.Combine(fixture.Data, "downloads", "versions", "other", "mods");
        Native.Check(string.Equals(Native.Full(Choose(null, instances[1])), Native.Full(expected), StringComparison.OrdinalIgnoreCase), "A stale cache overrode the currently selected instance.");
        Native.Check(Choose(null, null) == cached, "Unbound downloads lost deliberate custom-folder state.");
        var field = Native.Field(Native.Type("PCL.ModMinecraft"), "_McFolderSelected");
        object? previous = field.GetValue(null);
        try
        {
            string shared = Path.Combine(fixture.Data, "shared-minecraft") + Path.DirectorySeparatorChar;
            field.SetValue(null, shared);
            string setup = Path.Combine((string)Native.Property(instanceType, "PathVersion").GetValue(instances[0])!, "PCL", "Setup.ini");
            File.WriteAllText(setup, "VersionArgumentIndieV2:False\r\n", new System.Text.UTF8Encoding(false));
            Native.Static("PCL.ModBase", "IniClearCache", setup);
            ((IDictionary)Native.Get(Native.Entries["VersionArgumentIndieV2"]!, "ValueCache")!).Clear();
            Native.Check(string.Equals(Native.Full(Choose(instances[0], null)), Native.Full(Path.Combine(shared, "mods")), StringComparison.OrdinalIgnoreCase), "Non-isolated instance did not use the actual shared Minecraft directory.");
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }
}
