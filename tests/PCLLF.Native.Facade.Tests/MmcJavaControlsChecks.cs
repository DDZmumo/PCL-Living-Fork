using System.Windows;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcJavaControlsChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        var view = new GlassInstanceJavaPage(() => { });
        var controls = Tree(view).OfType<RadioButton>().ToArray();
        Native.Check(controls.Length == 4 && controls.Select(item => item.GroupName).Distinct().Count() == 1,
            "The native Java page lost its four mutually exclusive modes.");
        environment.AssertNoForbiddenIo();
    }
    internal static void RunSelector(FixtureEnvironment environment) => Run(environment);

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var leaf in Tree(child)) yield return leaf;
    }
}
