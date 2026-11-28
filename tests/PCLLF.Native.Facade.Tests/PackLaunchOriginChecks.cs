using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Real Main reports, launch ports and WPF view; no planner or graph implementation
// is copied. Only fixture files are created, and no network or game is started.
internal static class PackLaunchOriginChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Root, "pack-launch-origin");
        Directory.CreateDirectory(root);
        Native.Static("PCL.ModSecret", "ThemeRefresh", 0);
        string version = Path.Combine(root, "version"), game = Path.Combine(root, "isolated-game"), mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);
        string owner = Jar(mods, "author.jar", "author", "1.0.0", new JObject { ["missing_api"] = ">=1" });
        JObject manifest = Manifest(new JObject {
            ["mods/author.jar"] = new JObject { ["hash"] = Digest(owner), ["identity"] = "modrinth:author-project", ["fileIdentity"] = "modrinth:original-version" },
            ["mods/author-missing.jar"] = new JObject { ["hash"] = "sha1:" + new string('a', 40) }
        });
        WriteManifest(version, manifest);
        PackDependencyOrigin origin = ReadOrigin(version, game);
        Native.Check(origin.IsPack && origin.Complete && origin.NeedsAttention && origin.Files.Any(file => file.Role == PackDependencyFileRole.AuthorMissing),
            "PACK-LAUNCH-01: a missing author file must remain an integrity concern in a complete baseline.");
        Native.Check(Native.Full(origin.VersionFolder) != Native.Full(origin.GameRoot), "PACK-LAUNCH-01-path: fixture did not separate version and actual game directories.");

        object context = Activator.CreateInstance(Native.Type("PCL.ModDependencyInspector+InspectionContext"), true)!;
        Native.Set(context, "MinecraftVersion", "1.20.1"); Native.Set(context, "JavaVersion", "17"); Native.Set(context, "LoaderKnown", true);
        ((IDictionary)Native.Get(context, "LoaderVersions")!).Add("fabricloader", "0.16.14");
        object report = Native.Static("PCL.ModDependencyInspector", "ScanRaw", mods, context, CancellationToken.None, true)!;
        Native.Set(report, "PackOrigin", origin);
        LegacyLaunchDependencyReport port = ReportPort(mods, report);
        Native.Check(port.HasErrors && port.NeedsAttention, "PACK-LAUNCH-02: the author baseline incorrectly waived a genuine missing dependency.");
        FrameworkElement integrity = View(report, null);
        string integrityText = Text(integrity);
        Native.Check(integrityText.Contains("作者基线", StringComparison.Ordinal) && integrityText.Contains("作者文件缺失", StringComparison.Ordinal),
            "PACK-LAUNCH-03: the genuine view did not explain the author baseline and missing author file.");
        Render(integrity, Path.Combine(root, "integrity.png"));

        // The source concern is independent of HasErrors, and a legacy record
        // must not re-enter generic automatic activation/download planning.
        string completeOwner = Jar(mods, "author.jar", "author", "1.0.0", new JObject());
        manifest["files"]!["mods/author.jar"]!["hash"] = Digest(completeOwner);
        manifest["schema"] = 1; manifest.Remove("complete");
        WriteManifest(version, manifest);
        object legacyReport = Native.Static("PCL.ModDependencyInspector", "ScanRaw", mods, context, CancellationToken.None, true)!;
        Native.Set(legacyReport, "PackOrigin", ReadOrigin(version, game));
        LegacyLaunchDependencyReport legacyPort = ReportPort(mods, legacyReport);
        Native.Check(!legacyPort.HasErrors && legacyPort.NeedsAttention,
            "PACK-LAUNCH-04: incomplete author provenance must not be fabricated as a loader error or silently ignored.");
        LegacyLaunchDependencyPlan restricted = legacyPort.BuildAutoFill(CancellationToken.None);
        Native.Check(restricted.ItemsCount == 0 && restricted.EnablesCount == 0,
            "PACK-LAUNCH-04-plan: a legacy origin entered an automatic download or activation plan.");
        string legacyText = Text(View(legacyReport, restricted.Source));
        Native.Check(legacyText.Contains("不自动", StringComparison.Ordinal) && legacyText.Contains("原始整合包", StringComparison.Ordinal),
            "PACK-LAUNCH-04-view: the restricted origin was not explained in the actual view.");

        // This is the same bridge used by McLaunchDependencyCheck after choice 1.
        // A plan carries no implicit author-replacement consent before invocation.
        object plan = Activator.CreateInstance(Native.Type("PCL.ModDependencyAutoFill+Plan"), true)!;
        Native.Set(plan, "Root", mods); Native.Set(plan, "Report", legacyReport);
        object item = Activator.CreateInstance(Native.Type("PCL.ModDependencyAutoFill+Item"), true)!;
        object candidate = Activator.CreateInstance(typeof(ResourceVersion), true)!;
        Native.Set(candidate, "Display", "2.0.0-test"); Native.Set(candidate, "FileName", "author-2.jar");
        Native.Set(item, "Name", "Author library"); Native.Set(item, "Version", candidate);
        Native.Set(item, "Replacement", new DependencyReplacementData { Source = owner, BeforeHash = Digest(owner), AuthorRelative = "mods/author.jar", ProjectIdentity = "modrinth:author-project" });
        ((IList)Native.Get(plan, "Items")!).Add(item);
        LegacyLaunchDependencyPlan replacement = (LegacyLaunchDependencyPlan)Native.Static("PCL.ModLaunch", "DependencyPlan", plan)!;
        Native.Check(!(bool)Native.Get(plan, "AuthorReplacementConfirmed")! && replacement.ActionText.Contains("替换", StringComparison.Ordinal),
            "PACK-LAUNCH-05: a newly presented author candidate was already confirmed or had an ordinary-add caption.");
        FrameworkElement replacementView = View(legacyReport, plan);
        string replacementText = Text(replacementView);
        Native.Check(replacementText.Contains("需确认的作者模组版本替换", StringComparison.Ordinal) && replacementText.Contains("2.0.0-test", StringComparison.Ordinal) &&
            replacementText.Contains("原作者基线保留", StringComparison.Ordinal), "PACK-LAUNCH-05-view: the genuine view hid the replacement version or baseline consequence.");
        replacement.ConfirmAuthorReplacement!.Invoke();
        Native.Check((bool)Native.Get(plan, "AuthorReplacementConfirmed")!, "PACK-LAUNCH-05-confirm: explicit launch confirmation did not reach the actual plan.");
        Render(replacementView, Path.Combine(root, "replacement.png"));
        Native.Check(JObject.Parse(File.ReadAllText(Path.Combine(version, "PCL", "ModpackOrigin.json")))["schema"]!.Value<int>() == 1 &&
            !Directory.Exists(Path.Combine(version, "PCL", "PackUpdateBackups")), "PACK-LAUNCH-readonly: constructing the origin report/view changed the baseline or started a file transaction.");
        ContextReadsRealVersionDirectory(Path.Combine(root, "context"));
        Console.WriteLine("PASS pack launch provenance: PACK-LAUNCH-01..06 (actual Main origin/ports, integrity vs graph errors, schema-1 restriction, explicit replacement and WPF render).");
    }

    private static void ContextReadsRealVersionDirectory(string root)
    {
        string folder = Path.Combine(root, "versions", "fixture");
        Directory.CreateDirectory(Path.Combine(folder, "PCL"));
        File.WriteAllText(Path.Combine(folder, "fixture.json"), new JObject {
            ["id"] = "fixture", ["clientVersion"] = "1.20.1", ["type"] = "release", ["mainClass"] = "fixture.Main",
            ["libraries"] = new JArray(), ["arguments"] = new JObject { ["game"] = new JArray(), ["jvm"] = new JArray() }
        }.ToString());
        File.WriteAllText(Path.Combine(folder, "PCL", "Setup.ini"), "VersionArgumentIndieV2:True\r\n");
        File.WriteAllText(Path.Combine(folder, "PCL", "config.json"), "{\"VersionArgumentIndieV2\":true}");
        ModMinecraft.McInstance instance = new(folder); FileTaskScope.CacheInstance(instance); instance.Load();
        Native.Check(instance.State != ModMinecraft.McInstance.McInstanceState.Error, "PACK-LAUNCH-06-instance: the actual instance fixture is invalid.");
        Directory.CreateDirectory(Path.Combine(instance.PathIndie, "mods"));
        JObject manifest = Manifest(new JObject { ["mods/missing.jar"] = new JObject { ["hash"] = "sha1:" + new string('b', 40) } });
        WriteManifest(instance.PathVersion, manifest);
        LegacyLaunchDependencyContext context = (LegacyLaunchDependencyContext)Native.Static("PCL.ModLaunch", "DependencyContext", instance, "17", Path.Combine(instance.PathIndie, "mods"))!;
        LegacyLaunchDependencyReport checkedReport = context.Inspect(CancellationToken.None);
        Native.Check((bool)Native.Get(Native.Get(checkedReport.Source, "Context")!, "IgnoreJavaRequirements")!, "Launch context still checks Java as a mod requirement.");
        Native.Check((bool)Native.Get(checkedReport.Source, "OnlineAttempted")!, "Launch context skipped platform comparison.");
        PackDependencyOrigin origin = (PackDependencyOrigin)Native.Get(checkedReport.Source, "PackOrigin")!;
        Native.Check(origin.IsPack && origin.Complete && checkedReport.NeedsAttention &&
            Native.Full(origin.VersionFolder) == Native.Full(instance.PathVersion) && Native.Full(origin.GameRoot) == Native.Full(instance.PathIndie),
            "PACK-LAUNCH-06: the actual context ignored the version origin or inspected another instance's game folder.");
    }

    private static LegacyLaunchDependencyReport ReportPort(string mods, object report) =>
        (LegacyLaunchDependencyReport)Native.Static("PCL.ModLaunch", "DependencyReport", mods, report)!;
    private static FrameworkElement View(object report, object? plan)
    {
        Type type = Native.Type("PCL.GlassModDependencyCheckView");
        return (FrameworkElement)Activator.CreateInstance(type, Native.Flags, null, new object?[] { "启动前模组检查", report, plan }, null)!;
    }
    private static string Text(FrameworkElement root)
    {
        // ScrollViewer applies its template during layout. Looking only at the
        // unmeasured visual tree would omit the actual confirmation groups.
        root.Measure(new Size(740, 500)); root.Arrange(new Rect(0, 0, 740, 500)); root.UpdateLayout();
        return string.Join("\n", Descendants(root).OfType<TextBlock>().Select(block => block.Text)
            .Concat(Descendants(root).OfType<TextBlock>().Select(hint => hint.Text)));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Render(FrameworkElement view, string file)
    {
        view.Measure(new Size(740, 500)); view.Arrange(new Rect(0, 0, 740, 500)); view.UpdateLayout();
        RenderTargetBitmap image = new(740, 500, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(file); png.Save(output);
    }
    private static JObject Manifest(JObject files) => new() {
        ["schema"] = 2, ["complete"] = true, ["name"] = "Fixture pack", ["version"] = "1", ["minecraft"] = "1.20.1",
        ["loaders"] = new JObject { ["Fabric"] = "0.16.14" }, ["files"] = files
    };
    private static void WriteManifest(string version, JObject manifest)
    {
        Directory.CreateDirectory(Path.Combine(version, "PCL"));
        File.WriteAllText(Path.Combine(version, "PCL", "ModpackOrigin.json"), manifest.ToString());
    }
    private static PackDependencyOrigin ReadOrigin(string version, string game) => PackDependencyOrigin.Read(version, game, false,
        path => (JObject?)Native.Static("PCL.ModpackUpdate", "ReadManifest", path),
        (path, algorithm, token) => (string?)Native.Static("PCL.ModpackUpdate", "DigestForInspection", path, algorithm, token));
    private static string Digest(string path) => "sha1:" + Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Jar(string mods, string name, string id, string version, JObject dependencies)
    {
        string path = Path.Combine(mods, name);
        using FileStream output = File.Create(path); using ZipArchive archive = new(output, ZipArchiveMode.Create);
        using StreamWriter writer = new(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false));
        writer.Write(new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = version, ["name"] = id, ["depends"] = dependencies }.ToString());
        return path;
    }
}
