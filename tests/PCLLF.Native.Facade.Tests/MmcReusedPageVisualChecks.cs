using System.Windows;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcReusedPageVisualChecks
{
    internal static void RunLight(FixtureEnvironment environment) => Run(environment, 0);
    internal static void RunDark(FixtureEnvironment environment) => Run(environment, 1);

    private static void Run(FixtureEnvironment environment, int mode)
    {
        Native.Cache("UiLauncherColorMode", mode);
        var main = FileTaskShell.Install();
        var page = new GlassLaunchOptionsPage(main);
        page.RefreshPalette();
        page.Measure(new Size(860, 600));
        page.Arrange(new Rect(0, 0, 860, 600));
        Native.Check(page.Scroll is ScrollViewer, "Launch settings lost their native viewport.");
        environment.AssertNoForbiddenIo();
    }
}
