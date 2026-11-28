// SPDX-License-Identifier: MPL-2.0
using MeloongCore;
using PCLCS;

namespace PCL;

internal static class LauncherInstanceContext
{
    internal static ModMinecraft.McInstance Instance;
    internal static readonly RedoableWorker<Java> CurrentJavaWorker = MmcJavaDiscovery.CreateWorker(() => Instance);

    internal static void SyncInstanceContextIfLoaded() => ModMain.FrmMain?.SyncGlassInstanceContext();

    // The display snapshot can preserve an unreadable managed instance's UI. Operations revalidate it independently.
    internal static InstanceSidebarState ObserveInstanceContext(ModMinecraft.McInstance owner, bool preserveMmcFamily = false) =>
        InstanceSidebarContext.Observe(owner?.PathVersion, preserveMmcFamily || owner?.UsesMmcBackend == true ||
            owner?.Config.ContextStamp.Kind == MmcConfigurationContextKind.Managed);

    internal static bool IsMmcNavigationContext => ModMain.FrmMain?.GlassInstanceNavigation.Context.IsMmcFamily ??
        ObserveInstanceContext(Instance).IsMmcFamily;

    internal static bool IsEditingPage(FormMain.PageSubType route) => InstanceNavigationRules.IsEditingPage((int)route);

    internal static void ReloadCurrentJava()
    {
        if (ModAnimation.AniControlEnabled != 0 || Instance is not { } instance) return;
        MmcJavaDiscovery.Start(instance, CurrentJavaWorker);
    }
}
