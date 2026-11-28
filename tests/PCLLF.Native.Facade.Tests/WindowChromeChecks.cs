using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using PCL;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace PCLLF.Native.Facade.Tests;

// Load the exact product window markup without FormMain's startup/account lifecycle.
// Native controls and frame geometry are real; content cards use fixture text.
internal static class WindowChromeChecks
{
    internal static void Run(FixtureEnvironment environment) {
        using var view = new PCLLF.Wpf.Glass.GlassLauncherView();
        PCLLF.Native.Facade.Tests.Native.Check(view.FindName("BodyFrame") is System.Windows.Controls.Border,
            "The actual native workbench is missing its body surface.");
        environment.AssertNoForbiddenIo();
    }
}
