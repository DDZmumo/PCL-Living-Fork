using PCLLF.MMCC.Core;

internal static class MmcArchiveInspectionTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("MMC archive inspection chooses bounded direct metadata paths without parsing content", MetadataPaths);
        yield return ("MMC archive inspection reuses path collisions roles and ownership protections", Rejections);
        yield return ("MMC archive inspection checks metadata declared size before host byte reads", Budgets);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void MetadataPaths() {
        var result = MmcPackageReader.InspectArchive([new("pack/MMC-PACK.JSON", 20), new("pack/Instance.CFG", 10), new("pack/Patches/a.JSON", 30),
            new("pack/patches/nested/b.json", 20), new("pack/.minecraft/config/a"), new("pack/config/Foo/a"), new("pack/config/foo/b")]);
        Require(!result.HasErrors && result.RelativeRoot == "pack/" && result.GameRootLeaf == ".minecraft" && result.MetadataPaths.SequenceEqual(["pack/MMC-PACK.JSON", "pack/Instance.CFG", "pack/Patches/a.JSON"]), "Inspection changed source root/direct metadata selection.");
        Require(result.Diagnostics.Any(d => d.Code == "MMC_DIRECTORY_ALIAS"), "Directory alias diagnostic lost.");
    }
    private static void Rejections() {
        foreach (MmcArchiveEntry[] entries in new[] {
            new[] { new MmcArchiveEntry("mmc-pack.json"), new("second/mmc-pack.json") },
            new[] { new MmcArchiveEntry("mmc-pack.json"), new("mods/a"), new("MODS/A") },
            new[] { new MmcArchiveEntry("mmc-pack.json"), new("PCL/MmcSource/evil") },
            new[] { new MmcArchiveEntry("mmc-pack.json"), new("instance.cfg/", IsDirectory: true) },
            new[] { new MmcArchiveEntry("mmc-pack.json"), new("patches") }
        }) Require(MmcPackageReader.InspectArchive(entries).HasErrors, "Unsafe archive inspection passed.");
    }
    private static void Budgets() {
        var result = MmcPackageReader.InspectArchive([new("mmc-pack.json", 100)], new() { MaxMetadataBytes = 50 });
        Require(result.HasErrors && result.Diagnostics.Any(d => d.Code == "MMC_METADATA_BUDGET"), "Declared metadata size was not checked before reading.");
    }
}
