using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using Card = PCL.ModMinecraft.McInstanceCardType;
using Instance = PCL.ModMinecraft.McInstance;

namespace PCLLF.Native.Facade.Tests;

// Real Main cards and clone presenter, using owned fixtures only. No game or desktop interaction.
internal static class MmcSelectionGroupingChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        string root = Path.Combine(files.Root, "mmc-selection-groups"), game = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(game); files.SelectMinecraft(game);
        string managedRoot = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
        Instance managed = new Instance(managedRoot).Load();
        string ordinaryRoot = Path.Combine(game, "versions", "mmc-name-only"); Directory.CreateDirectory(ordinaryRoot);
        string ordinaryJson = Path.Combine(ordinaryRoot, "mmc-name-only.json");
        File.WriteAllText(ordinaryJson, """{"id":"1.7.10","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"releaseTime":"2014-06-26T09:00:00Z"}""", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(ordinaryRoot, "mmc-pack.json"), "loose metadata does not claim installed management");
        Instance ordinary = new Instance(ordinaryRoot).Load();
        Native.Check(managed.UsesMmcBackend && !ordinary.UsesMmcBackend, "Main did not distinguish installed management from a name/loose package manifest.");
        ordinary.Version.ModLoaderVersions[ModLoaders.Forge] = "10.13.4.1614"; ordinary.State = Instance.McInstanceState.Forge;
        Directory.CreateDirectory(Path.Combine(ordinaryRoot, "PCL"));
        File.WriteAllText(Path.Combine(ordinaryRoot, "PCL", "CloneOrigin.json"), new JObject {
            ["schema"] = 1, ["sourceVersionFolder"] = managedRoot, ["cloneName"] = ordinary.Name, ["sourceName"] = managed.Name
        }.ToString());
        var oldCards = ModMinecraft.McInstanceList;
        string primary = Path.Combine(managedRoot, "PCL", "InstanceDefinition.json"); byte[] primaryBefore = File.ReadAllBytes(primary);
        byte[] ordinaryBefore = File.ReadAllBytes(ordinaryJson);
        try {
            var page = new GlassInstanceSelectionPage(FileTaskShell.Install());
            Set(Card.API);
            Check("MMC 实例 (1)", "Forge 版本 (1)");
            managed.IsStar = ordinary.IsStar = true; Set(Card.Star);
            Check("MMC 实例 · 收藏 (1)", "收藏夹");
            managed.IsStar = ordinary.IsStar = false; managed.DisplayType = ordinary.DisplayType = Card.Hidden; Set(Card.Hidden);
            Native.Check(Render().Count == 0, "Hidden instances leaked into the ordinary selection view.");
            page.ShowHidden = true; Check("MMC 实例 · 隐藏 (1)", "隐藏的版本 (1)");
            page.ShowHidden = false; managed.DisplayType = ordinary.DisplayType = Card.Auto;
            MmcIniConfiguration.QueuePreferenceBatch(managed.Config, new Dictionary<string, string?> { ["IsStar"] = "True", ["DisplayType"] = "1" });
            managed.Config.Save();
            string projection = Path.Combine(managedRoot, Path.GetFileName(managedRoot) + ".json");
            File.AppendAllText(projection, " ");
            var fresh = (Dictionary<Card, List<Instance>>)Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithoutCache", game + Path.DirectorySeparatorChar)!;
            managed = fresh.Values.SelectMany(items => items).Single(item => item.Name == "managed");
            Native.Check(managed.IsStar && managed.DisplayType == Card.Hidden && managed.State == Instance.McInstanceState.Error && managed.UsesMmcBackend,
                "Cold-list loading a damaged projection lost identity-bound hidden/favorite preferences.");
            Set(Card.API); Native.Check(Render().Count == 1, "A hidden repair-required MMC instance leaked after a cold list refresh.");
            page.ShowHidden = true;
            Native.Check(Render().Single().Title == "MMC 实例 · 隐藏 (1)", "F11 could not reveal the hidden repair-required MMC instance separately.");
            page.ShowHidden = false;
            MmcIniConfiguration.QueuePreferenceBatch(managed.Config, new Dictionary<string, string?> { ["DisplayType"] = "0" }); managed.Config.Save();
            managed = new Instance(managedRoot).Load(); Set(Card.API); Check("MMC 实例 · 收藏 (1)", "Forge 版本 (1)");
            MmcIniConfiguration.QueuePreferenceBatch(managed.Config, new Dictionary<string, string?> { ["IsStar"] = "False" }); managed.Config.Save();
            managed = new Instance(managedRoot).Load(); Set(Card.API);
            Native.Check(managed.UsesMmcBackend && managed.State == Instance.McInstanceState.Error, "A damaged MMC projection fell back to ordinary management.");
            Check("MMC 实例 · 需要修复 (1)", "Forge 版本 (1)");
            Native.Check(File.ReadAllBytes(primary).SequenceEqual(primaryBefore) && File.ReadAllBytes(ordinaryJson).SequenceEqual(ordinaryBefore),
                "Rendering the separated selection cards changed an instance definition or ordinary version JSON.");

            void Set(Card category) => ModMinecraft.McInstanceList = new() { [category] = [managed, ordinary] };
            List<(string Title, FrameworkElement Element)> Render() {
                page.Open(loadData: false);
                Native.Call(typeof(GlassInstanceSelectionPage), "Render", page);
                var panel = (StackPanel)Native.Get(page, "_groups")!;
                return panel.Children.OfType<FrameworkElement>().Select(element =>
                    (Descendants(element).OfType<Expander>().FirstOrDefault()?.Header?.ToString() ?? string.Empty, element)).ToList();
            }
            void Check(string mmcTitle, string ordinaryTitle) {
                var groups = Render();
                Native.Check(groups.Select(item => item.Title).SequenceEqual(new[] { mmcTitle, ordinaryTitle }),
                    "The native selection changed the managed and ordinary groups.");
            }
        } finally { ModMinecraft.McInstanceList = oldCards; }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject item) {
        yield return item;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            foreach (DependencyObject element in Descendants(child)) yield return element;
    }
}
