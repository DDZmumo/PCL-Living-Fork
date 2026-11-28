using System.Windows.Controls;
using MeloongCore;
using PCL;

namespace PCLLF.Native.Facade.Tests;
// Assertions are the pinned Test-FileTaskNavigation.ps1 PageBack reproduction,
// through genuine Native controls/ports and its real PF controller.
internal static class FileTaskNavigationScenarios
{
    internal static void Run(FileTaskScope scope)
    {
        string minecraft = Path.Combine(scope.Root, "navigation", "minecraft");
        string game = Path.Combine(minecraft, "versions", "fixture");
        scope.SelectMinecraft(minecraft);
        Directory.CreateDirectory(Path.Combine(game, "PCL"));
        File.WriteAllText(Path.Combine(game, "fixture.json"), "{\"id\":\"fixture\",\"type\":\"release\",\"mainClass\":\"fixture.Main\",\"libraries\":[]}");
        File.WriteAllText(Path.Combine(game, "PCL", "Setup.ini"), "VersionArgumentIndieV2:True");
        ModMinecraft.McInstance instance = new(game);
        LauncherInstanceContext.Instance = instance;
        FileTaskScope.CacheInstance(instance);
        FormMain main = FileTaskShell.Install();
        RedoableWorker? worker = new((Action)(() =>
        {
        }));
        Native.Check((bool)Native.Call(typeof(ModMinecraft.McInstanceLock), "TryAcquireSnapshot", null, worker, instance.PathVersion)!, "Fixture snapshot reservation unavailable.");
        try
        {
            foreach (string mode in new[]
            {
                "snapshot",
                "completed"
            }

            )
            {
                if (mode == "completed")
                {
                    worker!.Start();
                    Native.Check(worker.WaitIfRunning(5000), "Navigation snapshot fixture did not stop.");
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (ModMinecraft.McInstanceLock.IsLocked(instance))
                    {
                        Native.Check(DateTime.UtcNow < deadline, "Stopped navigation worker retained its reservation.");
                        Thread.Sleep(1);
                    }

                    worker = null;
                }

                foreach (FormMain.PageSubType id in new[]
                {
                    FormMain.PageSubType.InstanceRestorePoints,
                    FormMain.PageSubType.InstanceClone,
                    FormMain.PageSubType.InstanceSaves
                }

                )
                {
                    main.PageChange(FormMain.PageType.InstanceSetup, id);
                    main.PageChange(FormMain.PageType.TaskManager);
                    main.PageBack();
                    Native.Check(main.PageCurrent.Page == FormMain.PageType.InstanceSetup && main.PageCurrentSub == id &&
                        main.GlassInstanceNavigation.Sidebar().Count(item => item.IsSelected) == 1,
                        mode + " task return did not preserve the originating route and sidebar: " + id);
                }

                if (mode == "snapshot")
                {
                    FormMain.PageSubType previous = main.PageCurrentSub;
                    main.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceSetup);
                    Native.Check(main.PageCurrentSub == previous, "A locked editor interrupted the current instance page.");
                }
            }
        }
        finally
        {
            if (worker is not null)
            {
                worker.Start();
                Native.Check(worker.WaitIfRunning(5000), "Navigation reservation cleanup did not finish.");
            }
        }
    }

}
