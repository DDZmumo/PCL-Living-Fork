using PCL;

namespace PCLLF.Native.Facade.Tests;

internal static class InstanceSidebarChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        using var files = new FileTaskScope(environment);
        var main = FileTaskShell.Install();
        string root = Path.Combine(files.Root, "sidebar", "versions", "fixture");
        Directory.CreateDirectory(root);
        LauncherInstanceContext.Instance = new ModMinecraft.McInstance(root);
        main.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceKeybindings);
        var items = main.GlassInstanceNavigation.Sidebar();
        Native.Check(items.Any(item => item.Label == "按键管理" && item.IsSelected && item.IconData.Length > 0),
            "The native sidebar lost the selected keybinding route or its icon.");
        Native.Check(main.GlassInstanceNavigation.GetPage(FormMain.PageSubType.InstanceKeybindings) is PageInstanceKeybindings,
            "The sidebar does not mount the native keybinding page.");
        environment.AssertNoForbiddenIo();
    }
}
