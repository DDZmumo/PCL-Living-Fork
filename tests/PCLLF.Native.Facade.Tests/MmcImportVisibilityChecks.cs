using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Genuine reservations and actual Main fresh/cached list paths; no game, network or player path mutation.
internal static class MmcImportVisibilityChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        string game = Path.Combine(files.Root, "import-list", "minecraft"), versions = Path.Combine(game, "versions");
        Directory.CreateDirectory(versions); files.SelectMinecraft(game);
        using var reservation = MmcOwnedImportReservation.Create(Path.Combine(versions, "future-instance"), Guid.NewGuid(), new string('a', 64));
        string temporary = reservation.TemporaryRoot;
        Native.Check(MmcImportVisibility.IsPrivateReservation(temporary), "Active genuine import reservation was not classified as private.");
        string marker = Path.Combine(temporary, "PCL", "MmcImportPending.json"); byte[] originalMarker = File.ReadAllBytes(marker);
        string foreign = Path.Combine(versions, ".pcllf-mmc-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(foreign, "PCL")); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "unknown bytes");
        var forged = JObject.Parse(Encoding.UTF8.GetString(originalMarker)); forged["TemporaryRoot"] = foreign;
        File.WriteAllText(Path.Combine(foreign, "PCL", "MmcImportPending.json"), forged.ToString());
        File.Copy(Path.Combine(temporary, ".pclignore"), Path.Combine(foreign, ".pclignore"));
        Native.Check(!MmcImportVisibility.IsPrivateReservation(foreign), "Self-asserted matching prefix/markers hid a foreign version without its independent owner.");
        var fresh = LegacyMinecraftDirectoryScanner.GetDirectories(versions);
        Native.Check(fresh.All(item => item.Name != Path.GetFileName(temporary)) && fresh.Any(item => item.Name == Path.GetFileName(foreign)), "Fresh list lost private/foreign import distinction.");
        string missing = ".pcllf-mmc-import-" + Guid.NewGuid().ToString("N");
        string ini = Path.Combine(game, "PCL.ini"); ModBase.WriteIni(ini, "CardCount", "1"); ModBase.WriteIni(ini, "CardKey1", "-1");
        ModBase.WriteIni(ini, "CardValue1", Path.GetFileName(temporary) + ":" + missing);
        var cached = (Dictionary<ModMinecraft.McInstanceCardType, List<ModMinecraft.McInstance>>?)Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithCache", game + Path.DirectorySeparatorChar);
        Native.Check(cached is not null && cached.Values.All(items => items.Count == 0) && !Directory.Exists(Path.Combine(versions, missing)), "Cached list revived a private or already removed import root.");
        File.WriteAllText(marker, "{}");
        Native.Check(!MmcImportVisibility.IsPrivateReservation(temporary) && LegacyMinecraftDirectoryScanner.GetDirectories(versions).Any(item => item.Name == Path.GetFileName(temporary)), "Damaged ownership disappeared instead of remaining diagnostic.");
        var oversized = JObject.Parse(Encoding.UTF8.GetString(originalMarker)); oversized["SchemaVersion"] = 2147483648L;
        File.WriteAllText(marker, oversized.ToString());
        Native.Check(!MmcImportVisibility.IsPrivateReservation(temporary) && LegacyMinecraftDirectoryScanner.GetDirectories(versions).Any(item => item.Name == Path.GetFileName(temporary)), "Out-of-range marker schema broke the list instead of retaining the diagnostic row.");
        File.WriteAllBytes(marker, originalMarker); reservation.Dispose();
        Native.Check(MmcImportVisibility.IsPrivateReservation(temporary) && MmcImportRecovery.Read(game).Entries.Any(entry => entry.TemporaryRoot == temporary && entry.CanRetry), "Canceled/unpublished private data lost its separate recovery entry.");
        Native.Check(File.ReadAllText(Path.Combine(foreign, "keep.txt")) == "unknown bytes" && File.ReadAllBytes(marker).SequenceEqual(originalMarker), "List classification mutated source bytes.");
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS MMC fresh/cached lists omit only independent-owner-bound private reservations and missing stale paths, retain forged/damaged roots and preserve initial-import recovery; no cleanup or game.");
    }
}
