using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class LaunchMoreSettingsChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        var main = FileTaskShell.Install();
        var launch = new GlassLaunchOptionsPage(main);
        Native.Check(launch.Scroll is ScrollViewer, "Launch options lost their native scrollable workspace.");
        main.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch);
        Native.Check(main.PageCurrentSub == FormMain.PageSubType.SetupLaunch, "The launch options route did not select its sidebar entry.");
        environment.AssertNoForbiddenIo();
    }
}
