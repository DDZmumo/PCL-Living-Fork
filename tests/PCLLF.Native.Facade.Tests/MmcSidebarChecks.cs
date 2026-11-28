using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcSidebarChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var files = new FileTaskScope(environment);
        string root = Path.Combine(files.Root, "mmc-sidebar");
        string game = Path.Combine(root, "minecraft");
        files.SelectMinecraft(game);
        string managedRoot = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
        var managed = new ModMinecraft.McInstance(managedRoot).Load();
        LauncherInstanceContext.Instance = managed;
        var main = FileTaskShell.Install();
        var navigation = main.GlassInstanceNavigation;

        Native.Check(navigation.Context.IsMmcFamily, "The managed instance lost its protected navigation family.");
        Native.Check(navigation.GetPage(FormMain.PageSubType.InstanceModify) is PageInstanceMmcComponents, "Managed modify must mount the component editor.");
        Native.Check(navigation.GetPage(FormMain.PageSubType.InstanceMmcMove) is PageInstanceMmcTool, "Managed move must mount its native tool.");
        Native.Check(navigation.Sidebar().Any(item => item.Label == "移动实例" && item.IsEnabled), "The managed sidebar lost the move action.");
        var selected = navigation.GetPage(FormMain.PageSubType.InstanceRestorePoints);
        string ordinary = Path.Combine(game, "versions", "ordinary");
        Directory.CreateDirectory(ordinary);
        LauncherInstanceContext.Instance = new ModMinecraft.McInstance(ordinary);
        Native.Check(!navigation.Context.IsMmcFamily && !ReferenceEquals(selected, navigation.GetPage(FormMain.PageSubType.InstanceRestorePoints)),
            "Changing family reused another instance's managed history page.");
        InstanceConfigProviders.FlushAndDisposeWithin(files.Root);
        environment.AssertNoForbiddenIo();
    }

    internal static void RunStates(FixtureEnvironment environment) => Run(environment);
    internal static void RunVisuals(FixtureEnvironment environment) => Run(environment);
    internal static void RunAdmission(FixtureEnvironment environment) => Run(environment);
}
