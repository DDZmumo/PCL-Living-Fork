using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static partial class DependencyCases<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile, TOptions, TCache>
{
    private static FrameworkElement LaunchCheckView(TReport report, object? plan)
        => (FrameworkElement)new GlassModDependencyCheckView("启动前模组检查", (ModDependencyInspector.InspectionReport)(object)report, (ModDependencyAutoFill.Plan?)plan);
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
        var owner = FileTaskShell.Install();
        var options = new GlassLaunchOptionsPage(owner);
        Native.Check(options.Scroll is ScrollViewer, "Launch dependency options lost their native viewport.");
    }

    private static void FieldInfoSetEnum(object instance, string name, string value)
    {
        System.Reflection.FieldInfo field = Native.Field(instance.GetType(), name); field.SetValue(instance, Enum.Parse(field.FieldType, value));
    }

    private static IEnumerable<string> LaunchViewText(DependencyObject root)
    {
        if (root is TextBlock label) yield return label.Text;
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (string caption in LaunchViewText(child)) yield return caption;
    }
}
