using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using PCL;

namespace PCLLF.Native.Facade.Tests;
internal static partial class ModpackModeChecks {
    private static readonly Type Isolation = Native.Type("PCL.ExportedVersionIsolation");
    private static object? Policy(string method, params object?[] args) => Native.Call(Isolation,method,null,args);
    private static string ReadSetting(string path, string key) => (string)Native.Static("PCL.ModBase","ReadIni",path,key,"")!;
    private static bool ChoiceEnabled(object? choice) => choice is not null && choice.GetType().Assembly == Native.Main && (bool)Native.Get(choice,"Enabled")!;
    private static void CheckIsolation(Scope scope, PageInstanceExport export) {
        E(31,Part<CheckBox>(export,"CheckAdvancedVersionIsolation").IsChecked == true);
        string root = Path.Combine(scope.Root,"isolation"), original = Path.Combine(root,"original","PCL","Setup.ini"); Put(original,"VersionArgumentIndieV2:False\r\nUnrelated:preserve-source\r\n"); byte[] originalBytes = File.ReadAllBytes(original);
        foreach (bool enabled in new[] { true,false }) {
            string pack = Path.Combine(root,"pack-" + enabled), overrides = Path.Combine(pack,"overrides"), settings = Path.Combine(overrides,"PCL","Setup.ini"); Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.Copy(original,settings);
            Put(Path.Combine(overrides,"mods","example.jar"),"synthetic-mod"); Put(Path.Combine(overrides,"saves","world","level.dat"),"synthetic-save"); Put(Path.Combine(overrides,"PCL","Logo.png"),"synthetic-logo"); Put(Path.Combine(overrides,"runtime","jre","bin","java.exe"),"synthetic-java-never-executed");
            Policy("ConfigureExport",overrides,enabled,new[] { "runtime/jre" });
            E(46,ReadSetting(settings,"VersionArgumentIndieV2") == enabled.ToString() && ReadSetting(settings,"VersionArgumentIndie") == (enabled ? "1" : "2")); E(47,ReadSetting(settings,"Unrelated") == "preserve-source");
            Put(Path.Combine(pack,"modrinth.index.json"),"{\"formatVersion\":1,\"dependencies\":{\"minecraft\":\"1.20.1\"},\"files\":[]}"); string mrpack = Path.Combine(root,"export-" + enabled + ".mrpack"); ZipFile.CreateFromDirectory(pack,mrpack);
            object? policy; using (ZipArchive archive = ZipFile.OpenRead(mrpack)) policy = Policy("ReadArchiveChoice",archive,""); E(53,policy is not null); E(54,ChoiceEnabled(policy) == enabled);
            foreach (string entryName in new[] { "overrides/PCL/Setup.ini", @"overrides\PCL\Setup.ini" }) {
                using MemoryStream buffer = new(); using (ZipArchive zip = new(buffer,ZipArchiveMode.Create,true)) { using StreamWriter writer = new(zip.CreateEntry(entryName).Open()); writer.Write(File.ReadAllText(settings)); }
                buffer.Position = 0; using ZipArchive read = new(buffer,ZipArchiveMode.Read,true); object? forwarded = Policy("ReadArchiveChoice",read,""); E(61,forwarded is not null && ChoiceEnabled(forwarded) == enabled);
            }
            string launcherPack = Path.Combine(root,"launcher-" + enabled); Directory.CreateDirectory(launcherPack); File.Copy(mrpack,Path.Combine(launcherPack,"modpack.mrpack")); string launcherZip = Path.Combine(root,"launcher-" + enabled + ".zip"); ZipFile.CreateFromDirectory(launcherPack,launcherZip);
            object? wrapped; using (ZipArchive outer = ZipFile.OpenRead(launcherZip)) { using Stream stream = outer.GetEntry("modpack.mrpack")!.Open(); using MemoryStream buffer = new(); stream.CopyTo(buffer); buffer.Position = 0; using ZipArchive inner = new(buffer,ZipArchiveMode.Read,true); wrapped = Policy("ReadArchiveChoice",inner,""); } E(73,wrapped is not null && ChoiceEnabled(wrapped) == enabled);
            string mcRoot = Path.Combine(root,"import-" + enabled,".minecraft"), version = Path.Combine(mcRoot,"versions","fixture"), versionPath = version + Path.DirectorySeparatorChar; scope.SetCache("VersionArgumentIndieV2",!enabled,versionPath);
            Policy("ConfigureTarget",version,policy); E(79,!Cache("VersionArgumentIndieV2").Contains(versionPath)); string game = (string)Policy("GameFolder",version,mcRoot,policy)!; E(81,game == (enabled ? version : mcRoot));
            Policy("CopyOverrides",overrides,version,game,policy); Policy("CopyOverrides",Path.Combine(pack,"client-overrides"),version,game,policy);
            E(84,File.ReadAllText(Path.Combine(game,"mods","example.jar")) == "synthetic-mod" && File.ReadAllText(Path.Combine(game,"saves","world","level.dat")) == "synthetic-save"); foreach (string relative in new[] { "PCL/Logo.png","runtime/jre/bin/java.exe" }) E(85,File.Exists(Path.Combine(version,relative)));
            E(86,enabled || (!Directory.Exists(Path.Combine(version,"mods")) && !Directory.Exists(Path.Combine(mcRoot,"PCL")) && !Directory.Exists(Path.Combine(mcRoot,"runtime")))); E(87,ReadSetting(Path.Combine(version,"PCL","Setup.ini"),"VersionArgumentIndieV2") == enabled.ToString());
            bool rejected = false; try { Policy("OverrideTarget","../escape.txt",version,mcRoot,policy); } catch (InvalidDataException) { rejected = true; } E(90,rejected);
            if (!enabled) foreach (string reserved in new[] { "versions/other/file.json","libraries/other.jar","assets/indexes/fixture.json" }) { rejected = false; try { Policy("OverrideTarget",reserved,version,mcRoot,policy); } catch (InvalidDataException) { rejected = true; } E(95,rejected); }
            // Archive bytes are closed before exclusive reopen. Nothing in this
            // fixture ever executes the synthetic Java or an exported launcher.
            using (FileStream exclusive = new(mrpack,FileMode.Open,FileAccess.Read,FileShare.None)) { } using (FileStream exclusive = new(launcherZip,FileMode.Open,FileAccess.Read,FileShare.None)) { }
        }
        string legacy = Path.Combine(root,"legacy"), legacySettings = Path.Combine(legacy,"overrides","PCL","Setup.ini"); Put(legacySettings,"VersionArgumentIndieV2:False\r\n"); string legacyZip = Path.Combine(root,"legacy.mrpack"); ZipFile.CreateFromDirectory(legacy,legacyZip);
        object? legacyChoice; using (ZipArchive archive = ZipFile.OpenRead(legacyZip)) legacyChoice = Policy("ReadArchiveChoice",archive,""); E(105,legacyChoice is null);
        string legacyTarget = Path.Combine(root,"legacy-target"); Native.Static("PCL.ModModpack","CopyOverrideDirectory",Path.Combine(legacy,"overrides"),legacyTarget,null,null,null); E(108,ReadSetting(Path.Combine(legacyTarget,"PCL","Setup.ini"),"VersionArgumentIndieV2") == "True");
        E(109,File.ReadAllBytes(original).SequenceEqual(originalBytes) && Equals(Cached("LaunchArgumentIndieV2"),3));
    }
}
