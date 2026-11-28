// SPDX-License-Identifier: MPL-2.0
using PCLCS;
using McInstance = PCL.ModMinecraft.McInstance;

namespace PCL;

internal static class LauncherMemory
{
    private static readonly LegacySetupLaunchMemory<McInstance> Global = new(
        key => Settings.Get<object>(key), () => My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory,
        instance => instance.IsLoaded, instance => instance.Load(), instance => instance.Modable,
        instance => instance.Version.HasOptiFine, instance => instance.PathIndie);

    internal static double ForGlobal(McInstance instance, bool useVersionJavaSetup) => Global.GetRam(instance, useVersionJavaSetup);

    internal static double ForInstance(McInstance instance) =>
        InstanceRamPolicy.Get<McInstance, McInstance.McInstanceState, ModMinecraft.McInstanceCardType>(instance,
            (key, target) => Settings.Get<int>(key, Instance: target),
            () => My.MyWpfExtension.Computer.Info.AvailablePhysicalMemory,
            target => ForGlobal(target, true));
}
