using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using PCLCS;
using ItemRules = PCLCS.ResourceItemController<PCL.ResourceProject, PCL.ResourceSearcher.SearchResult, PCL.ResourceSearcher.SearchRequest,
    PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.ModAnimation.AniData>;

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

        using var page = new PCL.GlassResourceDetailPage(FileTaskShell.Install(),
            [project, new List<string>(), "", ModLoaders.None, ResourceTypes.Map, null!]);
        var loader = Native.Get(page, "_loader")!;
        Native.Set(loader, "Output", items);
        var controller = Native.Get(page, "_detail")!;
        Type detail = controller.GetType();
        Native.Set(controller, "Project", project);
        foreach (ResourceTypes type in new[]
        {
            (ResourceTypes)0,
            ResourceTypes.Map,
            ResourceTypes.ResourcePack
        }

        )
        {
            Native.Set(controller, "TargetResourceType", type);
            IList visible = (IList)Native.Call(detail, "GetResults", controller)!;
            Native.Check(visible.Count == items.Count, "Main detail filter lost map files for " + type);
            for (int i = 0; i < items.Count; i++)
                Native.Check(ReferenceEquals(visible[i], items[i]), "Main detail filter copied live version models.");
        }

        Native.Check(controller is not null && controller.GetType().Assembly.GetName().Name == "PCLLF.WPF.Core" && controller.GetType().GetGenericArguments()[0] == project.GetType() && controller.GetType().GetGenericArguments()[1] == version, "Page adapter did not use the actual production controller and actual Main models.");
        Native.Check(ReferenceEquals(Native.Get(controller!, "Project"), project), "Detail adapter created a second project store.");
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
        Directory.CreateDirectory(path);
        object instance = type.GetConstructor([typeof(string)])!.Invoke([path]);
        Native.Property(type, "Version").SetValue(instance, new McVersion("1.20.1", true));
        return instance;
    }

    private static object[] Build(object project, object page, object? search, object[]? existing, object? instance) =>
        ItemRules.BuildDetailArguments((PCL.ResourceProject)project, (int)(PCL.FormMain.PageType)page,
            (PCL.ResourceSearcher.SearchRequest?)search, existing, (PCL.ModMinecraft.McInstance?)instance,
            (int)PCL.FormMain.PageType.Download, (int)PCL.FormMain.PageType.ResourceDetail, (int)PCL.FormMain.PageType.InstanceSetup);
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
        Type detail = Native.Field(typeof(PCL.GlassResourceDetailPage), "_detail").FieldType;
        string cached = Path.Combine(fixture.Data, "downloads", "minecraft");
        string Choose(object? target, object? selected) => (string)Native.Call(detail, "ChooseModDownloadFolder", null, target, selected, cached, suitable)!;
        string expected = Path.Combine(fixture.Data, "downloads", "versions", "selected", "mods");
        Native.Check(string.Equals(Native.Full(Choose(instances[0], instances[1])), Native.Full(expected), StringComparison.OrdinalIgnoreCase), "Cache overrode the explicit instance mods directory.");
        expected = Path.Combine(fixture.Data, "downloads", "versions", "other", "mods");
        Native.Check(string.Equals(Native.Full(Choose(null, instances[1])), Native.Full(expected), StringComparison.OrdinalIgnoreCase), "A stale cache overrode the currently selected instance.");
        Native.Check(Choose(null, null) == cached, "Unbound downloads lost deliberate custom-folder state.");
        var field = Native.State(Native.Type("PCL.ModMinecraft"), "_McFolderSelected");
        object? previous = field.GetValue(null);
        try
        {
            string shared = Path.Combine(fixture.Data, "shared-minecraft") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(shared);
            field.SetValue(null, shared);
            // Exercise the real coordinated preference setter. Rewriting Setup.ini
            // behind the already attached provider leaves its valid snapshot unchanged.
            PCL.Settings.Set("VersionArgumentIndieV2", false, (PCL.ModMinecraft.McInstance)instances[0]);
            Native.Check(string.Equals(Native.Full(Choose(instances[0], null)), Native.Full(Path.Combine(shared, "mods")), StringComparison.OrdinalIgnoreCase), "Non-isolated instance did not use the actual shared Minecraft directory.");
        }
        finally
        {
            field.SetValue(null, previous);
        }
    }
}
