using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// LF f4/main50 existing MMC behavior only. The game-install request is captured
// at its original host boundary; libraries, real Java and game launch are pending.
internal static class MmcImportChecks
{
    private static readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private static void Check(string id, bool condition) { Native.Check(condition, "MMC original contract " + id); seen.Add(id); }
    internal static void Run(FixtureEnvironment environment) {
        MmcFoundationChecks.Run(environment);
        seen.Clear(); using MmcImportScope scope = new(environment);
        FullPack(scope, "root", "", true, false);
        FullPack(scope, "nested", "Pack/", false, true);
        ParseAndRefusal(scope); NameAndAdmission(scope); CancelAndTraversal(scope);
        string[] required = ["entry", "plan", "components", "roots", "files", "ini", "commands", "server", "logo", "jvm", "preserve", "archive-release", "nested", "missing-info", "bad-json", "missing-components", "unsupported-cancel", "unsupported-skip", "lwjgl", "invalid-name", "first-line-name", "duplicate", "cancel-token", "cancel-output", "traversal", "bounds"];
        Native.Check(seen.SetEquals(required), "MMC contract checks were omitted: " + string.Join(',', required.Except(seen)));
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-import-contract: actual Main format dispatcher/MMC parser, typed component request and real pack task/ZIP/files/config/cancel/path boundaries. Captured game-install dependency; no game libraries, Java selection, HTTP, process or completed root-install claim.");
    }
    private static JObject Manifest() => new() { ["formatVersion"] = 1, ["components"] = new JArray {
        new JObject { ["uid"]="net.minecraft", ["version"]="1.20.1" },
        new JObject { ["uid"]="net.minecraftforge", ["version"]="47.3.0" },
        new JObject { ["uid"]="net.neoforged", ["version"]="20.1.0" },
        new JObject { ["uid"]="net.fabricmc.fabric-loader", ["version"]="0.16.0" },
        new JObject { ["uid"]="org.lwjgl3", ["version"]="3.3.1" }, new JObject { ["version"]="ignored-no-uid" }
    } };
    private static string Config(string name, bool replaceJvm) => "[General]\nname=" + name + "\nOverrideCommands=true\nPreLaunchCommand=\\\"$INST_JAVA\\\" \\\"$INST_MC_DIR\\mods\\\" \\\"$INST_DIR\\config\\\" $INST_ID $INST_NAME\nJoinServerOnLaunch=true\nJoinServerOnLaunchAddress=mc.example.invalid:25565\niconKey=launcher\nJvmArgs=-Dpack=a=b\nOverrideJavaArgs=" + (replaceJvm ? "true" : "false") + "\n";
    private static string Zip(MmcImportScope scope, string name, IEnumerable<(string Name, byte[] Bytes)> entries) {
        string path = Path.Combine(scope.Root, name + ".zip"); using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (var item in entries) { using Stream stream = zip.CreateEntry(item.Name).Open(); stream.Write(item.Bytes); } return path;
    }
    private static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));
    private static string SimpleZip(MmcImportScope scope, string id, JObject? manifest = null, string? config = null, params (string Name, byte[] Bytes)[] extra) {
        List<(string,byte[])> entries = [ Text("mmc-pack.json", (manifest ?? Manifest()).ToString()), Text("instance.cfg", config ?? Config(id, true)) ]; entries.AddRange(extra); return Zip(scope, id, entries);
    }
    private static string Ini(string folder, string key) => ModBase.ReadIni(Path.Combine(folder, "PCL", "Setup.ini"), key, "");
    private static void FullPack(MmcImportScope scope, string id, string prefix, bool bothRoots, bool appendJvm) {
        string name = "Mmc-" + id; List<(string,byte[])> entries = [Text(prefix+"mmc-pack.json",Manifest().ToString()),Text(prefix+"instance.cfg",Config(name,!appendJvm))];
        string source = bothRoots ? "minecraft/" : ".minecraft/";
        entries.Add(Text(prefix+source+"mods/keep.txt","actual-pack-data")); entries.Add(Text(prefix+source+"options.txt","gamma:1.0"));
        entries.Add(Text(prefix+source+"PCL/Setup.ini","VersionArgumentIndieV2:False\r\nIsStar:True\r\nUnknownFixture:keep-me\r\n"));
        if(bothRoots)entries.Add(Text(prefix+".minecraft/mods/keep.txt","wrong-root-must-not-win"));
        byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");entries.Add((prefix+"launcher.png",png));
        string zip=Zip(scope,id,entries); byte[] original=File.ReadAllBytes(zip); int prompts=scope.PromptCount,messages=scope.Messages.Count;
        var plan=scope.Plan(zip); var request=scope.Requests.Last(); string folder=Path.Combine(scope.Game,"versions",name);
        Check("entry",plan.GetType().Assembly==Native.Main&&plan.Name=="MMC 整合包安装："+name+" "&&scope.PromptCount==prompts);
        Check("plan",request.Name==name&&Native.Full(request.Folder)==Native.Full(folder)&&!plan.Loaders[0].Show&&!plan.Loaders[0].Block&&!plan.Loaders[1].Show);
        var components=request.Components;Check("components",components.MinecraftVersion=="1.20.1"&&components.ModLoaderVersions.Count==3&&components.ModLoaderVersions[ModLoaders.Forge]=="47.3.0"&&components.ModLoaderVersions[ModLoaders.NeoForge]=="20.1.0"&&components.ModLoaderVersions[ModLoaders.Fabric]=="0.16.0"&&components.OptiFineEntry is null);
        Check("lwjgl",scope.Messages.Count==messages);
        var task=scope.PackTask(plan);scope.Execute(task,request.Folder);
        Check("roots",File.ReadAllText(Path.Combine(folder,"mods","keep.txt"))=="actual-pack-data");
        Check("files",File.ReadAllText(Path.Combine(folder,"options.txt"))=="gamma:1.0");
        Check("ini",Ini(folder,"VersionArgumentIndie")=="1"&&Ini(folder,"VersionArgumentIndieV2")=="True"&&Ini(folder,"IsStar")=="False");
        const string expectedCommand = "\"{java}java.exe\" \"{minecraft}mods\" \"{verpath}config\" {name} {name}";
        string actualCommand = Ini(folder,"VersionAdvanceRun");
        if (actualCommand != expectedCommand) CommandDiagnostics(scope, prefix, name, !appendJvm, actualCommand, expectedCommand);
        Check("commands",actualCommand==expectedCommand);
        Check("server",Ini(folder,"VersionServerEnter")=="mc.example.invalid:25565");
        Check("logo",Ini(folder,"LogoCustom")=="True"&&Ini(folder,"Logo")=="PCL\\Logo.png"&&File.ReadAllBytes(Path.Combine(folder,"PCL","Logo.png")).SequenceEqual(png));
        Check("jvm",Ini(folder,"VersionAdvanceJvm")=="-Dpack=a=b"+(appendJvm?" -Dglobal=kept":""));
        Check("preserve",Ini(folder,"UnknownFixture")=="keep-me"&&File.ReadAllBytes(zip).SequenceEqual(original));
        using(FileStream exclusive=new(zip,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}Check("archive-release",true);
        if(prefix.Length>0)Check("nested",File.Exists(Path.Combine(folder,"mods","keep.txt"))&&!Directory.Exists(Path.Combine(folder,"Pack")));
    }
    // Only the GUID-owned synthetic package/config is read after its genuine
    // workload/thread has returned. This exposes no user command or profile and
    // preserves the fixed LF command gold; diagnostics never repair the result.
    private static void CommandDiagnostics(MmcImportScope scope, string prefix, string name, bool replaceJvm, string actual, string expected) {
        static string Synthetic(string value) => Newtonsoft.Json.JsonConvert.SerializeObject(value.Length <= 2048 ? value : value[..2048] + "<truncated>");
        string input = Config(name, replaceJvm).Split('\n').Single(line => line.StartsWith("PreLaunchCommand=", StringComparison.Ordinal));
        string file = Path.Combine(scope.LastTemp, prefix.Replace('/', Path.DirectorySeparatorChar), "instance.cfg");
        string owned = Native.Full(scope.Root) + Path.DirectorySeparatorChar;
        Native.Check(Native.Full(file).StartsWith(owned, StringComparison.OrdinalIgnoreCase), "MMC diagnostic input escaped its owned synthetic root.");
        try {
            string converted = File.Exists(file) ? File.ReadAllText(file) : "<missing synthetic config>";
            string readFlag = ModBase.ReadIni(file, "OverrideCommands", "<missing-key>"), readCommand = ModBase.ReadIni(file, "PreLaunchCommand", "<missing-key>");
            string setup = Path.Combine(scope.Game, "versions", name, "PCL", "Setup.ini");
            Native.Check(Native.Full(setup).StartsWith(owned, StringComparison.OrdinalIgnoreCase), "MMC diagnostic output escaped its owned synthetic root.");
            string stored = File.Exists(setup) ? File.ReadLines(setup).FirstOrDefault(line => line.StartsWith("VersionAdvanceRun:", StringComparison.Ordinal)) ?? "<missing command line>" : "<missing synthetic Setup.ini>";
            Console.WriteLine("DIAG MMC synthetic command: actual=" + Synthetic(actual) + "; expected=" + Synthetic(expected) + "; input=" + Synthetic(input) + "; convertedConfig=" + Synthetic(converted) + "; cachedOverride=" + Synthetic(readFlag) + "; cachedCommand=" + Synthetic(readCommand) + "; storedCommand=" + Synthetic(stored));
        } catch (Exception error) { Console.WriteLine("DIAG MMC synthetic command diagnostic failed: " + error.GetType().Name); }
    }
    private static Exception Rejected(Func<object?> action) { try { action(); } catch(Exception error) { return error; } throw new InvalidOperationException("Expected existing MMC refusal did not occur."); }
    private static void ParseAndRefusal(MmcImportScope scope) {
        string missing=Zip(scope,"missing-cfg",[Text("mmc-pack.json",Manifest().ToString())]);Check("missing-info",Rejected(()=>ModModpack.ModpackInstall(missing)).Message=="MMC 整合包安装信息存在问题");
        string bad=Zip(scope,"bad-json",[Text("mmc-pack.json","{broken"),Text("instance.cfg",Config("BadJson",true))]);Check("bad-json",Rejected(()=>ModModpack.ModpackInstall(bad)).Message=="MMC 整合包安装信息存在问题");
        string noComponents=SimpleZip(scope,"no-components",new JObject{["formatVersion"]=1});Check("missing-components",Rejected(()=>ModModpack.ModpackInstall(noComponents)).Message=="该 MMC 整合包未提供游戏版本信息，无法安装！");
        JObject unsupported=Manifest();((JArray)unsupported["components"]!).Add(new JObject{["uid"]="existing-unsupported-loader",["version"]="1"});
        int requests=scope.Requests.Count,plans=scope.Plans.Count;scope.MessageResult=1;string canceled=SimpleZip(scope,"unsupported-cancel",unsupported);
        Check("unsupported-cancel",Rejected(()=>ModModpack.ModpackInstall(canceled)) is OperationCanceledException&&scope.Requests.Count==requests&&scope.Plans.Count==plans&&scope.Messages.Last().Title=="不兼容的加载器"&&scope.Messages.Last().Button1=="取消");
        scope.MessageResult=2;string skipped=SimpleZip(scope,"unsupported-skip",unsupported);var skip=scope.Plan(skipped);
        Check("unsupported-skip",scope.Requests.Last().Components.ModLoaderVersions.Count==3&&scope.Messages.Last().Button2=="不安装 existing-unsupported-loader 并继续"&&skip.Loaders.Count==2);
    }
    private static void NameAndAdmission(MmcImportScope scope) {
        scope.PromptResult=null;int requests=scope.Requests.Count;string invalid=SimpleZip(scope,"invalid-name",config:Config("../outside",true));
        Check("invalid-name",Rejected(()=>ModModpack.ModpackInstall(invalid)) is OperationCanceledException&&scope.LastCandidate=="../outside"&&scope.Requests.Count==requests);
        scope.PromptResult="FirstLineSelected";int prompts=scope.PromptCount;string first=SimpleZip(scope,"first-line",config:"name=FirstLineOriginal\n");var plan=scope.Plan(first);
        Check("first-line-name",scope.PromptCount==prompts+1&&scope.LastCandidate==""&&scope.Requests.Last().Name=="FirstLineSelected"&&plan.Name=="MMC 整合包安装：FirstLineSelected ");scope.PromptResult=null;
        scope.DuplicateName="MMC 整合包安装：Duplicate ";int plans=scope.Plans.Count;string duplicate=SimpleZip(scope,"duplicate",config:Config("Duplicate",true));
        Check("duplicate",Rejected(()=>ModModpack.ModpackInstall(duplicate)) is OperationCanceledException&&scope.Plans.Count==plans&&scope.Hints.Last()==("该整合包正在安装中！",true));scope.DuplicateName=null;
    }
    private static void CancelAndTraversal(MmcImportScope scope) {
        string cancel=SimpleZip(scope,"cancel",extra:[Text("minecraft/options.txt","must-not-copy")]);var plan=scope.Plan(cancel);var task=scope.PackTask(plan);string temp=scope.LastTemp;File.WriteAllText(Path.Combine(temp,"owned-before-cancel"),"original LF clears temp before cancellation");scope.ExecuteCanceled(task);
        Check("cancel-token",task.State==ModBase.LoadState.Canceled);Check("cancel-output",!Directory.Exists(temp)&&!Directory.Exists(scope.Requests.Last().Folder));
        string traversal=SimpleZip(scope,"traversal",extra:[Text("../escaped-mmc.txt","must-never-write")]);var bad=scope.Plan(traversal);string unpack=scope.LastTemp;Exception error=scope.ExecuteRejected(scope.PackTask(bad));
        Check("traversal",error is InvalidDataException);Check("bounds",!File.Exists(Path.Combine(Path.GetDirectoryName(unpack.TrimEnd(Path.DirectorySeparatorChar))!,"escaped-mmc.txt"))&&!Directory.Exists(scope.Requests.Last().Folder));
    }
}
