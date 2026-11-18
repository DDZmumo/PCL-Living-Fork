using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class MmcQuarantineListChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope tasks = new(environment);
        string root = Path.Combine(tasks.Root, "quarantine-list"), game = Path.Combine(root, "minecraft");
        Directory.CreateDirectory(game); tasks.SelectMinecraft(game);
        string source = (string)Native.Call(typeof(MmcLifecycleChecks), "ImportOwned", null, root, game)!;
        var imported = MmcInstanceInspector.Read(source);
        var backend = new MmcInstanceLifecycleBackend();
        var deleted = backend.DeleteAsync(new(source, imported.Definition!.InstanceId, imported.Definition.Generation,
            imported.PrimarySha256!)).GetAwaiter().GetResult();
        Native.Check(deleted.Status == MmcInstanceBackendStatus.Succeeded, "Owned list fixture delete failed: " + deleted.Failure);
        string quarantine = deleted.CurrentRoot!, versions = Path.GetDirectoryName(quarantine)!;
        string foreign = Path.Combine(versions, ".pcllf-mmc-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "unknown ordinary bytes");
        var listed = LegacyMinecraftDirectoryScanner.GetDirectories(versions);
        Native.Check(listed.All(item => item.Name != Path.GetFileName(quarantine)) && listed.Any(item => item.Name == Path.GetFileName(foreign)),
            "Fresh product scanner did not distinguish an owned completed quarantine from a foreign prefix. Probe=" +
            MmcInstanceLifecycleVisibility.IsCompletedQuarantine(quarantine) + "; actual=" + string.Join(";", listed.Select(item => item.FullName)) + "; expected=" + quarantine);
        // A stale cache must be filtered independently of the fresh directory inventory.
        string ini = Path.Combine(game, "PCL.ini");
        ModBase.WriteIni(ini, "CardCount", "1"); ModBase.WriteIni(ini, "CardKey1", "-1");
        ModBase.WriteIni(ini, "CardValue1", Path.GetFileName(quarantine));
        var cached = (Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>>?)
            Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithCache", game + Path.DirectorySeparatorChar);
        Native.Check(cached is not null && cached.Values.All(items => items.Count == 0), "The actual cached list revived an owned quarantine.");
        string marker = Path.Combine(quarantine, "PCL", "MmcLifecyclePending.json"); byte[] markerBytes = File.ReadAllBytes(marker);
        File.WriteAllText(marker, "{}");
        Native.Check(LegacyMinecraftDirectoryScanner.GetDirectories(versions).Any(item => item.Name == Path.GetFileName(quarantine)),
            "Damaged lifecycle evidence disappeared from the product scanner.");
        File.WriteAllBytes(marker, markerBytes);
        var restored = backend.RestoreDeletedAsync(deleted.OperationDirectory!).GetAwaiter().GetResult();
        Native.Check(restored.Status == MmcInstanceBackendStatus.Succeeded && LegacyMinecraftDirectoryScanner.GetDirectories(versions).Any(item => item.Name == Path.GetFileName(source)),
            "Restored source did not reappear in the product scanner.");
        Native.Check(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "unknown ordinary bytes", "Foreign prefix data changed.");
        InstanceConfigProviders.FlushAndDisposeWithin(root); environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-quarantine-list: actual Main fresh scanner and stale cache omit only hash/owner/decision-bound completed quarantines; foreign, damaged and restored directories remain visible.");
    }
}
