using System.Collections;
using System.Text;
using System.Windows;
using PCL;
using Application = System.Windows.Application;

namespace PCLLF.Native.Facade.Tests;
internal static class VisualAssetIconChecks
{
    private const string Official = "pack://application:,,,/Plain Craft Launcher 2;component/Images/";
    private const string Living = "pack://application:,,,/LivingPCL;component/Images/";
    private static readonly string[] Icons = { "Anvil.png", "CobbleStone.png", "CommandBlock.png", "Egg.png", "Fabric.png", "GoldBlock.png", "Grass.png", "GrassPath.png", "NeoForge.png", "OptiFabric.png", "RedstoneBlock.png", "RedstoneLampOff.png", "RedstoneLampOn.png" };
    private static readonly string[] Custom = { @"PCL\Logo.png", @"D:\custom\Grass.png", "https://example.invalid/Grass.png", "pack://application:,,,/Other;component/Images/Blocks/Grass.png", Living + "Blocks/Unknown.png", Living + "Blocks/../Grass.png", Living + "Blocks/Grass.png?custom=1", Living + "Heads/Logo.png" };
    private static string Helper(string name, params object?[] args) => (string)Native.Static("PCL.MinecraftLogoCompatibility", name, args)!;
    private static void Clear(string path) => ModBase.IniClearCache(path);
    private static string? Read(string path, string key) => ModBase.ReadIni(path, key, null!);
    private static void Put(string path, string logo, string custom = "True")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"Logo:{logo}\r\nLogoCustom:{custom}\r\nUnrelated:preserve\r\n"); Clear(path);
    }
    internal static void Run(VisualAssetChecks.Scope scope)
    {
        string root = Path.Combine(scope.Root, "icons"); Directory.CreateDirectory(root);
        VisualAssetChecks.Check("icon", 33, Native.Main.GetName().Name == "LivingPCL");
        foreach (string file in Icons)
        {
            string runtime = MyBitmap.NormalizeResourceUri(Official + "Blocks/" + file);
            VisualAssetChecks.Check("icon", 38, runtime == Living + "Blocks/" + file);
            var resource = Application.GetResourceStream(new Uri(runtime));
            using (Stream? stream = resource?.Stream) VisualAssetChecks.Check("icon", 40, stream is not null && stream.Length > 0);
            string expected = Official + "Blocks/" + file;
            foreach (string value in new[] { Living + "Blocks/" + file, "/LivingPCL;component/Images/Blocks/" + file, "pack://application:,,,/images/Blocks/" + file, "pack://application:,,,/Plain%20Craft%20Launcher%202;component/Images/Blocks/" + file, expected })
                VisualAssetChecks.Check("icon", 107, Helper("ToStorageUri", value) == expected, file + " input=" + value);
        }
        foreach (string custom in Custom) VisualAssetChecks.Check("icon", 111, Helper("ToStorageUri", custom) == custom, custom);
        string setup = Path.Combine(root, "versions", "old", "PCL", "Setup.ini");
        string bad = Living + "Blocks/Fabric.png", good = Official + "Blocks/Fabric.png";
        foreach (string customFlag in new[] { "True", "False" })
        {
            Put(setup, bad, customFlag); string repaired = Helper("ReadVersionLogo", setup, "");
            VisualAssetChecks.Check("icon", 116, repaired == good && Read(setup, "Logo") == repaired);
            VisualAssetChecks.Check("icon", 117, Read(setup, "LogoCustom") == customFlag && Read(setup, "Unrelated") == "preserve");
        }
        foreach (string custom in Custom)
        {
            Put(setup, custom); string before = File.ReadAllText(setup);
            VisualAssetChecks.Check("icon", 122, Helper("ReadVersionLogo", setup, "") == custom && File.ReadAllText(setup) == before);
        }
        string freshVersion = Path.Combine(root, "versions", "new");
        // The first-logo assertion covers a missing Setup.ini in an established instance.
        // An unqualified INI write must not recreate a missing version directory.
        Directory.CreateDirectory(freshVersion);
        string fresh = Path.Combine(freshVersion, "PCL", "Setup.ini");
        VisualAssetChecks.Check("icon", 125, Helper("ReadVersionLogo", fresh, "fallback") == "fallback" && !File.Exists(fresh));
        Native.Static("PCL.MinecraftLogoCompatibility", "WriteVersionLogo", fresh, Living + "Blocks/Grass.png");
        VisualAssetChecks.Check("icon", 127, Read(fresh, "Logo") == Official + "Blocks/Grass.png");
        for (int cycle = 0; cycle < 6; cycle++)
        {
            string value = Helper("ReadVersionLogo", fresh, "");
            File.WriteAllText(fresh, $"Logo:{value}\r\nLogoCustom:True\r\nUnrelated:preserve\r\n"); Clear(fresh);
            Native.Static("PCL.MinecraftLogoCompatibility", "WriteVersionLogo", fresh, value);
            VisualAssetChecks.Check("icon", 134, Read(fresh, "Logo") == Official + "Blocks/Grass.png");
        }
        string sharedA = Path.Combine(root, "shared-a"), sharedB = Path.Combine(root, "shared-b");
        string brokenA = Path.Combine(sharedA, "versions", "a", "PCL", "Setup.ini"), brokenB = Path.Combine(sharedB, "versions", "b", "PCL", "Setup.ini"), unicodeSetup = Path.Combine(sharedB, "versions", "unicode", "PCL", "Setup.ini"), lockedSetup = Path.Combine(sharedA, "versions", "locked", "PCL", "Setup.ini"), customSetup = Path.Combine(sharedA, "versions", "custom", "PCL", "Setup.ini");
        Put(brokenA, bad, "False"); Put(brokenB, bad); Put(lockedSetup, bad); Put(customSetup, "https://example.invalid/custom.png");
        Directory.CreateDirectory(Path.GetDirectoryName(unicodeSetup)!);
        string unicodeText = $"# comments-preserve\r\nLogo:{bad}\r\nLogoCustom:True\r\nUnrelated:preserve\r\n";
        File.WriteAllText(unicodeSetup, unicodeText, Encoding.Unicode);
        string customBefore = File.ReadAllText(customSetup), rootCache = Path.Combine(sharedA, "PCL.ini"); File.WriteAllText(rootCache, "CardValue1:preserve");
        _ = Read(brokenA, "Logo"); // Warm the actual Main INI cache before repair.
        string missing = Path.Combine(root, "missing-root"); string[] folders = { sharedA, sharedB, sharedA.ToUpperInvariant(), missing };
        int count;
        using (FileStream locked = File.Open(lockedSetup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            count = (int)Native.Static("PCL.MinecraftLogoCompatibility", "RepairFolders", new object?[] { folders })!;
        VisualAssetChecks.Check("icon", 161, count == 3);
        foreach (string path in new[] { brokenA, brokenB, unicodeSetup }) VisualAssetChecks.Check("icon", 162, Read(path, "Logo") == good);
        VisualAssetChecks.Check("icon", 163, Read(brokenA, "LogoCustom") == "False" && File.ReadAllText(customSetup) == customBefore && File.ReadAllText(rootCache) == "CardValue1:preserve");
        byte[] unicode = File.ReadAllBytes(unicodeSetup);
        VisualAssetChecks.Check("icon", 164, File.ReadAllText(unicodeSetup) == unicodeText.Replace(bad, good, StringComparison.Ordinal) && unicode.Length >= 2 && unicode[0] == 255 && unicode[1] == 254);
        int retry = (int)Native.Static("PCL.MinecraftLogoCompatibility", "RepairFolders", new object?[] { folders })!, again = (int)Native.Static("PCL.MinecraftLogoCompatibility", "RepairFolders", new object?[] { folders })!;
        VisualAssetChecks.Check("icon", 165, retry == 1 && again == 0);
        VisualAssetChecks.Check("icon", 166, !Directory.Exists(missing) && !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        Instances(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) Native.Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Visual icon fixture contains an unexpected link.");
    }
    private static void Instances(string root)
    {
        ModMinecraft.McInstance property = new(Path.Combine(root, "versions", "property"));
        foreach (string custom in Custom)
        {
            property.Logo = custom;
            VisualAssetChecks.Check("icon", 173, (string?)Native.Property(property.GetType(), "LogoForStorage").GetValue(property) == custom);
        }
        string firstFolder = Path.Combine(root, "first", "versions", "1.20.1"); Directory.CreateDirectory(firstFolder);
        File.WriteAllText(Path.Combine(firstFolder, "1.20.1.json"), "{\"id\":\"1.20.1\",\"mainClass\":\"net.minecraft.client.main.Main\",\"type\":\"release\",\"releaseTime\":\"2023-06-12T13:25:51+00:00\",\"libraries\":[]}");
        ModMinecraft.McInstance first = new(firstFolder); first.Load();
        VisualAssetChecks.Check("icon", 181, Read(Path.Combine(firstFolder, "PCL", "Setup.ini"), "Logo") == Official + "Blocks/Grass.png");
        string mcRoot = Path.Combine(root, "cached"), setup = Path.Combine(mcRoot, "versions", "cached", "PCL", "Setup.ini");
        Put(setup, Living + "Blocks/GrassPath.png"); File.AppendAllText(setup, "State:1\r\nInfo:fixture\r\nVersionVanillaName:1.20.1\r\nReleaseTime:2023-06-12 13:25\r\n"); Clear(setup);
        File.WriteAllText(Path.Combine(mcRoot, "versions", "cached", "cached.json"), "{\"id\":\"1.20.1\",\"mainClass\":\"net.minecraft.client.main.Main\",\"type\":\"release\"}");
        string cards = Path.Combine(mcRoot, "PCL.ini"); File.WriteAllText(cards, "CardCount:1\r\nCardKey1:3\r\nCardValue1:cached:\r\nVersion:cached\r\nUnrelated:preserve\r\n"); string before = File.ReadAllText(cards);
        object? result = Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithCache", mcRoot + "\\");
        VisualAssetChecks.Check("icon", 192, result is IDictionary dictionary && dictionary.Count == 1);
        VisualAssetChecks.Check("icon", 193, Read(setup, "Logo") == Official + "Blocks/GrassPath.png");
        VisualAssetChecks.Check("icon", 194, File.ReadAllText(cards) == before);
        foreach (string custom in new[] { @"PCL\Logo.png", "https://example.invalid/custom.png" })
        {
            Put(setup, custom); File.AppendAllText(setup, "State:1\r\nVersionVanillaName:1.20.1\r\n"); Clear(setup);
            _ = Native.Static("PCL.ModMinecraft", "InitMcInstanceListWithCache", mcRoot + "\\");
            VisualAssetChecks.Check("icon", 200, Read(setup, "Logo") == custom && Read(setup, "LogoCustom") == "True");
        }
    }
}
