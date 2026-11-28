using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class LocalFeatureRecentChecks
{
    internal static void Run(LocalFeatureChecks.Scope scope)
    {
        var main = FileTaskShell.Install();
        main.PageChange(FormMain.PageType.Launch);
        Native.Check(main.PageCurrent.Page == FormMain.PageType.Launch, "Recent worlds are no longer hosted by the launcher entry.");
    }
}
