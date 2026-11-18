using PCLCS;
using PCLLF.MMCC.Core;

namespace PCLLF.Native.Facade.Tests;

// Original LF host checks, under the repository LICENCE. The MPL assembly is
// referenced as built; no Core implementation is linked into this executable.
internal static class MmcFoundationChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        const string manifest = """
            {"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}
            """;
        MmcPackageInput input = new(
            [new MmcArchiveEntry("mmc-pack.json", manifest.Length)],
            new Dictionary<string, string> { ["mmc-pack.json"] = manifest });
        MmcParseResult result = MmcPackageReader.Parse(input);
        Native.Check(!result.HasErrors && result.Package is not null,
            "The actual MPL Core must parse the supplied in-memory MMC source.");
        Native.Check(result.Package!.Components.Length == 1 &&
            result.Package.Components[0].Uid == "net.minecraft" &&
            result.Package.GameRootLeaf == "minecraft",
            "The actual Core must retain source component and game-root facts.");
        Native.Check(Native.Full(Path.GetDirectoryName(typeof(MmcPackageReader).Assembly.Location)!) ==
            Native.Full(AppContext.BaseDirectory),
            "The genuine copied Main output must supply the actual Core dependency.");

        MmcManagementDocument missing = MmcManagementDocument.Missing;
        MmcManagementResult legacy = MmcManagementReader.Evaluate(new(missing, missing, missing));
        Native.Check(legacy.State == MmcManagementState.Legacy && legacy.CanUseLegacyBackend,
            "An ordinary unmarked instance must remain on its existing backend.");
        MmcManagementResult damaged = MmcManagementReader.Evaluate(new(
            new MmcManagementDocument(true, "{"), missing, missing));
        Native.Check(damaged.State == MmcManagementState.ManagedMmcRepairRequired &&
            !damaged.CanUseLegacyBackend,
            "A damaged managed definition must never authorize ordinary repair.");
        string pendingRoot = Path.Combine(environment.Data, "mmc-pending-model", "versions", "Pending");
        Directory.CreateDirectory(Path.Combine(pendingRoot, "PCL"));
        File.WriteAllText(Path.Combine(pendingRoot, "PCL", "MmcImportPending.json"), "{broken");
        File.WriteAllText(Path.Combine(pendingRoot, ".pclignore"), "owned fixture");
        var pendingInstance = new PCL.ModMinecraft.McInstance(pendingRoot).Load();
        Native.Check(pendingInstance.IsLoaded && pendingInstance.Info.Contains("恢复", StringComparison.Ordinal) &&
            File.Exists(Path.Combine(pendingRoot, ".pclignore")) && !File.Exists(Path.Combine(pendingRoot, "Pending.json")),
            "Actual managed model must stay visible and readonly without ordinary JSON/ignore migration.");
        bool pathRefused = false;
        try { _ = pendingInstance.PathIndie; } catch (IOException) { pathRefused = true; }
        Native.Check(pathRefused, "Pending MMC model cannot fall back to the shared game directory.");
        bool renameRefused = false;
        try { pendingInstance.Rename("Other"); } catch (InvalidOperationException) { renameRefused = true; }
        Native.Check(renameRefused && Directory.Exists(pendingRoot), "Ordinary rename must not mutate an owned pending instance.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-foundations: actual Core assembly call and host ownership gate. " +
            "P1 source parsing only; no new MMC install, launch or repair integration claim.");
    }
}
