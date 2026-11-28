using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL;

namespace PCLLF.Native.Facade.Tests;
internal static class VisualAssetBackgroundChecks
{
    internal static void Run(FixtureEnvironment environment) {
        using var view = new PCLLF.Wpf.Glass.GlassLauncherView();
        PCLLF.Native.Facade.Tests.Native.Check(view.FindName("BodyFrame") is System.Windows.Controls.Border,
            "The actual native workbench is missing its body surface.");
        environment.AssertNoForbiddenIo();
    }
}
