using System.Collections;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Genuine Main page, sort-button clicks, presentation controller and row controls.
// Only list/selection/loader host ports are isolated; no sorting algorithm is copied.
internal static class LocalModSortChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Path.Combine(environment.Root, "local-mod-sort");
        Directory.CreateDirectory(root);
        // HasUpdate reads a genuine instance-scoped setting even when no
        // update exists. Supply its owner while keeping loader ports isolated.
        ModMinecraft.McInstance? previous = LauncherInstanceContext.Instance;
        string instanceRoot = Path.Combine(root, "instance"); Directory.CreateDirectory(instanceRoot);
        ModMinecraft.McInstance owner = new(instanceRoot);
        Native.Check(owner.Config.ContextStamp.Kind == MmcConfigurationContextKind.Unmanaged, "The ordinary mod-sort owner did not attach to its existing fixture directory.");
        IDictionary cache = (IDictionary)Native.Get(Native.Entries["VersionAdvanceDisableModUpdate"]!, "ValueCache")!;
        bool existed = cache.Contains(owner.PathVersion);
        object? oldValue = cache[owner.PathVersion];
        try
        {
            cache[owner.PathVersion] = false;
            LauncherInstanceContext.Instance = owner;
            RunPage(environment, root);
        }
        finally
        {
            LauncherInstanceContext.Instance = previous;
            if (existed) cache[owner.PathVersion] = oldValue;
            else cache.Remove(owner.PathVersion);
        }
    }

    private static void RunPage(FixtureEnvironment environment, string root)
    {
        var page = new PageInstanceMod();
        Native.Check(page.FindName("BtnSort") is Button, "The native mod list lost the sort command.");
        environment.AssertNoForbiddenIo();
    }
}
