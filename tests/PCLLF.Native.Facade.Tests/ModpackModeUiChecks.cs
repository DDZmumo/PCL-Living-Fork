using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static partial class ModpackModeChecks
{
    private static (PageInstanceExport Export, System.Windows.FrameworkElement Overview) CheckUi(Scope scope)
    {
        Native.Check(ModModpackMode.BlocksPage(FormMain.PageType.Download) == ModModpackMode.Enabled,
            "The mode policy and native route availability disagree.");
        return (new PageInstanceExport(), new PageInstanceOverall());
    }
}
