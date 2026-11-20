using System.Collections;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Presentation.Mods;

namespace PCLLF.Native.Facade.Tests;
internal static class ResourceUpdateChecks
{
    private static void C(int line,bool value)=>MapInstallSourceChecks.Check("update",line,value);
    private static object Kind(string name)=>Native.Enum("PCL.InstanceResourceKind",name)!;
    private static object? Updates(string method,params object?[] args)=>Native.Static("PCL.InstanceResourceUpdates",method,args);
    private static object? Value(object item,string name)=>item.GetType().GetField(name,Native.Flags) is { } field?field.GetValue(item):Native.Property(item.GetType(),name).GetValue(item);
    private static bool Has(object item)=>(bool)Value(item,"HasUpdate")!;
    private static string Hash(string file)=>Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static Exception Rejected(Action work){try{work();}catch(Exception error){return error;}throw new InvalidOperationException("Actual resource update unexpectedly accepted invalid work.");}
    private static void Zip(string file,string text){Directory.CreateDirectory(Path.GetDirectoryName(file)!);using ZipArchive zip=ZipFile.Open(file,ZipArchiveMode.Create);using StreamWriter writer=new(zip.CreateEntry("pack.mcmeta").Open());writer.Write(text);}
    internal static void Run(FixtureEnvironment environment)
    {
        MapInstallSourceChecks.Reset("update");using MapInstallResourceScope scope=new(environment,"resource-updates");
        string packs=Path.Combine(scope.Root,"resourcepacks"),original=Path.Combine(packs,"My selected pack.zip"),download=Path.Combine(scope.Root,"download","new.zip"),recycle=Path.Combine(scope.Root,"PCL","ResourceRecycle","ResourcePacks");
        Zip(original,"{\"pack\":{\"pack_format\":15,\"description\":\"Original\"}}");Zip(download,"{\"pack\":{\"pack_format\":15,\"description\":\"Updated\"}}");
        string oldHash=Hash(original),newHash=Hash(download);byte[] oldBytes=File.ReadAllBytes(original);long newSize=new FileInfo(download).Length;
        object packKind=Kind("ResourcePacks"),shaderKind=Kind("ShaderPacks");IList items=(IList)Native.Static("PCL.InstanceResourceStore","Scan",packs,packs,packKind,CancellationToken.None)!;
        JObject old=JObject.Parse("""{"id":"old","project_id":"project","name":"Version 1","version_number":"1.0","date_published":"2025-01-01T00:00:00Z","version_type":"release","downloads":1,"game_versions":["1.20.1"],"loaders":["minecraft"],"dependencies":[],"files":[]}""");
        JObject FileData(string name,string hash,long size)=>new(){["filename"]=name,["url"]="https://cdn.modrinth.com/data/project/"+name,["size"]=size,["primary"]=true,["hashes"]=new JObject{["sha1"]=hash}};
        old["files"]=new JArray(FileData("Old.zip",oldHash,oldBytes.Length));JObject candidate=(JObject)old.DeepClone();candidate["id"]="new";candidate["name"]="Version 2";candidate["version_number"]="2.0";candidate["date_published"]="2025-02-01T00:00:00Z";candidate["files"]=new JArray(FileData("New.zip",newHash,newSize));
        JObject cfOld=JObject.Parse("""{"id":1234567,"gameId":432,"modId":42,"displayName":"CF 1","fileName":"Old.zip","fileDate":"2025-01-01T00:00:00Z","releaseType":1,"downloadCount":1,"gameVersions":["1.20.1"],"downloadUrl":"https://edge.forgecdn.net/files/1234/567/Old.zip","fileFingerprint":7,"dependencies":[],"isAvailable":true}""");
        cfOld["fileLength"]=oldBytes.Length;cfOld["hashes"]=new JArray(new JObject{["algo"]=1,["value"]=oldHash});JObject cfNew=(JObject)cfOld.DeepClone();cfNew["id"]=1234568;cfNew["displayName"]="CF 2";cfNew["fileName"]="New.zip";cfNew["fileLength"]=newSize;cfNew["fileDate"]="2025-02-01T00:00:00Z";cfNew["downloadUrl"]="https://edge.forgecdn.net/files/1234/568/New.zip";cfNew["hashes"]=new JArray(new JObject{["algo"]=1,["value"]=newHash});
        string mode="modrinth",projectType="resourcepack";List<string> calls=new();
        Func<string,HttpMethod,string,JToken> request=(url,method,body)=>{
            calls.Add(url);JToken answer;
            if(url=="https://api.modrinth.com/v2/version_files")answer=mode=="modrinth"?new JObject{[oldHash]=old.DeepClone()}:new JObject();
            else if(url.StartsWith("https://api.modrinth.com/v2/projects?",StringComparison.Ordinal))answer=new JArray(new JObject{["id"]="project",["title"]="Matched pack",["project_type"]=projectType});
            else if(url=="https://api.modrinth.com/v2/version_files/update_individual"){C(66,body.Contains("1.20.1",StringComparison.Ordinal)&&body.Contains(projectType=="shader"?"iris":"minecraft",StringComparison.Ordinal)&&body.Contains("version_types",StringComparison.Ordinal));answer=new JObject{[oldHash]=candidate.DeepClone()};}
            else if(url=="https://api.curseforge.com/v1/fingerprints/432"){if(mode=="failure")throw new IOException("Synthetic API outage");answer=new JObject{["data"]=new JObject{["exactMatches"]=mode=="curseforge"?new JArray(new JObject{["id"]=42,["file"]=cfOld.DeepClone()}):new JArray()}};}
            else if(url=="https://api.curseforge.com/v1/mods")answer=JObject.Parse("""{"data":[{"id":42,"name":"CF pack","isAvailable":true,"allowModDistribution":true,"links":{"websiteUrl":"https://www.curseforge.com/minecraft/texture-packs/fixture"}}]}""");
            else if(url.StartsWith("https://api.curseforge.com/v1/mods/42/files?",StringComparison.Ordinal)){C(75,url.Contains("gameVersion=1.20.1",StringComparison.Ordinal));answer=new JObject{["data"]=new JArray(cfNew.DeepClone()),["pagination"]=new JObject{["totalCount"]=1}};}
            else throw new InvalidOperationException("Unexpected controlled API request: "+url);
            return answer;
        };
        Func<string,uint> fingerprint=_=>7;
        IList Check(object? kind=null)=>(IList)Updates("Check",packs,items,kind??packKind,"1.20.1",CancellationToken.None,request,fingerprint)!;
        Action<List<ModNet.NetFile>,CancellationToken> downloader=(files,token)=>{foreach(ModNet.NetFile file in files){token.ThrowIfCancellationRequested();File.Copy(download,file.LocalPath);}};
        void Install(IList infos,Action<List<ModNet.NetFile>,CancellationToken>? transfer=null,CancellationToken token=default)=>Updates("Install",packs,infos,packKind,"1.20.1",token,transfer??downloader);
        IList result=Check();C(82,result.Count==1&&Has(result[0]!)&&(string)Value(Value(result[0]!,"UpdateVersion")!,"Hash")! ==newHash);
        foreach(string field in new[]{"game_versions","loaders","project_id","version_type","date_published"}){JToken saved=candidate[field]!.DeepClone();candidate[field]=field switch{"game_versions"=>new JArray("1.21.1"),"loaders"=>new JArray("fabric"),"project_id"=>new JValue("foreign"),"version_type"=>new JValue("alpha"),_=>new JValue("2024-01-01T00:00:00Z")};C(87,!Has(Check()[0]!));candidate[field]=saved;}
        projectType="mod";C(90,!Has(Check()[0]!));projectType="resourcepack";
        old["files"]![0]!["primary"]=false;((JArray)old["files"]!).Add(new JObject{["filename"]="Primary.zip",["primary"]=true,["hashes"]=new JObject{["sha1"]=new string('a',40)}});C(92,!Has(Check()[0]!));old["files"]=new JArray(old["files"]![0]!.DeepClone());old["files"]![0]!["primary"]=true;
        mode="curseforge";result=Check();C(95,Has(result[0]!)&&(string)Value(Value(result[0]!,"UpdateVersion")!,"Hash")! ==newHash);
        JToken savedHash=cfOld["hashes"]!.DeepClone();cfOld["hashes"]=new JArray(new JObject{["algo"]=1,["value"]=new string('a',40)});C(97,!Has(Check()[0]!));cfOld["hashes"]=savedHash;
        JToken savedUrl=cfNew["downloadUrl"]!.DeepClone();cfNew["downloadUrl"]=null;C(99,!Has(Check()[0]!));cfNew["downloadUrl"]=savedUrl;
        JToken savedGames=cfNew["gameVersions"]!.DeepClone();cfNew["gameVersions"]=new JArray("1.21.1");C(101,!Has(Check()[0]!));cfNew["gameVersions"]=savedGames;
        mode="failure";C(102,((string)Value(Check()[0]!,"Status")!).Contains("CurseForge",StringComparison.Ordinal));
        mode="modrinth";projectType="shader";old["loaders"]=new JArray("iris","optifine");candidate["loaders"]=new JArray("iris","optifine");C(105,Has(Check(shaderKind)[0]!));
        projectType="resourcepack";old["loaders"]=new JArray("minecraft");candidate["loaders"]=new JArray("minecraft");result=Check();
        C(109,(string)Updates("FileHash",download,false)! ==newHash);C(110,(string)Value(Value(result[0]!,"UpdateVersion")!,"Hash")! ==newHash&&Convert.ToInt64(Value(Value(result[0]!,"UpdateVersion")!,"Size"))==newSize);
        Install(result);C(112,Hash(original)==newHash);C(113,!File.Exists(Path.Combine(packs,"New.zip")));
        C(115,!Directory.Exists(recycle));
        File.WriteAllBytes(original,oldBytes);items=(IList)Native.Static("PCL.InstanceResourceStore","Scan",packs,packs,packKind,CancellationToken.None)!;result=Check();
        Action<List<ModNet.NetFile>,CancellationToken> corrupt=(files,_)=>{foreach(ModNet.NetFile file in files)File.WriteAllText(file.LocalPath,"corrupt-download");};C(119,Rejected(()=>Install(result,corrupt)) is not null);C(120,Hash(original)==oldHash);
        IOException sentinel=new("Synthetic download failure");Action<List<ModNet.NetFile>,CancellationToken> failed=(_,_)=>throw sentinel;Exception observed=Rejected(()=>Install(result,failed));C(122,ReferenceEquals(observed,sentinel));
        using(CancellationTokenSource canceled=new()){canceled.Cancel();C(124,Rejected(()=>Install(result,token:canceled.Token)) is OperationCanceledException);}
        File.AppendAllText(original,"changed-after-check");C(126,Rejected(()=>Install(result)) is not null);C(127,!Directory.EnumerateDirectories(packs,".pcl-import-*").Any());
        MapInstallSourceChecks.Complete("update");environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native resource-updates: real Main MR/CF matching, direct replacement without recycle copies, error/cancel/stale filename preservation; injected transport only, no HTTP.");
        ModUpdateWithNewRequiredDependency(scope.Root);
        LocalModSortChecks.Run(environment);
    }

    private static void ModUpdateWithNewRequiredDependency(string root)
    {
        string fixture = Path.Combine(root, "mod-update-required-dependency");
        var owner = BackupResourceChecks.Instance(Path.Combine(fixture, "game", "versions", "neoforge-fixture"));
        owner.Version = new McVersion("1.21.1", true);
        owner.Version.ModLoaderVersions[ModLoaders.NeoForge] = "21.1.265";
        string mods = Path.Combine(owner.PathIndie, "mods");
        Directory.CreateDirectory(mods);
        string old = Path.Combine(mods, "beyonddimensions-0.7.30.jar");
        string candidate = Path.Combine(fixture, "beyonddimensions-0.8.10.jar");
        ModJar(old, "beyonddimensions", "0.7.30");
        ModJar(candidate, "beyonddimensions", "0.8.10", """
            [[dependencies.beyonddimensions]]
            modId="compixel"
            type="required"
            versionRange="[0.1.12,0.2)"
            side="BOTH"
            """);
        string originalHash = Hash(old);
        var selected = new LocalResourceFile(old) {
            ProjectVersion = new ResourceVersion { FileName = Path.GetFileName(old) },
            UpdateFile = new ResourceVersion {
                FileName = Path.GetFileName(candidate), Size = (int)new FileInfo(candidate).Length,
                Hash = Hash(candidate), DownloadUrls = ["https://cdn.modrinth.com/data/fixture/version.jar"]
            }
        };
        var previousInstance = PageInstanceLeft.Instance;
        try {
            PageInstanceLeft.Instance = owner;
            var page = new PageInstanceMod();
            object host = Native.Get(Native.Get(page, "Native")!, "host")!;
            Action<CancellationToken>? replace = null;
            Action<LegacyModUpdateJob>? onStateChanged = null;
            string? dialogue = null;
            int captures = 0;
            var reachedCapture = new InvalidOperationException("Reached history capture");
            Native.Set(host, "HistoryBusy", new Func<bool>(() => false));
            Native.Set(host, "ModpackMode", new Func<bool>(() => false));
            Native.Set(host, "ReadBoolean", new Func<string, bool>(_ => true));
            Native.Set(host, "TempPath", new Func<string>(() => fixture + Path.DirectorySeparatorChar));
            Native.Set(host, "UpdatingFolders", new Func<List<string>>(() => []));
            Native.Set(host, "LoaderRun", new Func<int, bool>(_ => false));
            Native.Set(host, "RefreshDownloadButton", new Action(() => { }));
            Native.Set(host, "RibbleDownloadButton", new Action(() => { }));
            Native.Set(host, "Background", new Action<Action, string, ThreadPriority>((_, _, _) => { }));
            Native.Set(host, "Message", new Func<string, string, string, string, bool, int>((body, title, _, _, _) => {
                Native.Check(title is "Mod 更新需要前置" or "核对新版必需前置", "A blocked update did not show its dependency dialog.");
                dialogue = body;
                return 1;
            }));
            Native.Set(host, "CaptureHistory", new Action<ModMinecraft.McInstance, string, CancellationToken>((_, _, _) => {
                captures++;
                throw reachedCapture;
            }));
            Native.Set(host, "CreateUpdateJob", new Func<Func<string>, List<ModNet.NetFile>, double, Action<CancellationToken>, LegacyModUpdateJob>((_, files, _, apply) => {
                string staged = files.Single().LocalPath;
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(candidate, staged, true);
                replace = apply;
                return new LegacyModUpdateJob { Start = () => { }, AddTaskbar = () => { }, SetOnStateChanged = changed => onStateChanged = changed };
            }));
            page.UpdateMods([selected]);
            Native.Check(replace is not null, "The real bulk update entry did not plan a downloaded replacement.");
            Exception failure = Rejected(() => replace!(CancellationToken.None));
            Native.Check(captures == 0 && failure.Message.Contains("compixel", StringComparison.Ordinal) &&
                failure.Message.Contains("[0.1.12,0.2)", StringComparison.Ordinal),
                "The bulk update should stop before replacing a Mod with a newly missing required dependency: " + failure.Message);
            Native.Check(File.Exists(old) && Hash(old) == originalHash && Directory.GetFiles(mods).Length == 1,
                "Rejected Mod update changed the installed files.");
            onStateChanged!(new LegacyModUpdateJob { State = () => 3, Error = () => new Exception("Replace failed", failure) });
            Native.Check(dialogue?.Contains("compixel", StringComparison.Ordinal) == true,
                "The real Mod update page did not explain the missing dependency.");

            string disabled = Path.Combine(mods, "compixel.jar.disabled");
            ModJar(disabled, "compixel", "0.1.18");
            Native.Check(Rejected(() => replace!(CancellationToken.None)).Message.Contains("compixel", StringComparison.Ordinal),
                "A disabled dependency satisfied the proposed enabled configuration.");

            string enabled = Path.Combine(mods, "compixel.jar");
            ModJar(enabled, "compixel", "0.2.1");
            Native.Check(Rejected(() => replace!(CancellationToken.None)).Message.Contains("版本不匹配", StringComparison.Ordinal),
                "The real update entry accepted an out-of-range dependency.");
            File.Delete(enabled);
            File.Move(disabled, enabled);
            ModJar(Path.Combine(mods, "unrelated.jar"), "unrelated", "1.0.0", """
                [[dependencies.unrelated]]
                modId="already_missing"
                type="required"
                versionRange="*"
                side="BOTH"
                """);
            Native.Check(ReferenceEquals(Rejected(() => replace!(CancellationToken.None)), reachedCapture) && captures == 1,
                "A compatible enabled dependency or an unrelated pre-existing issue blocked the update.");
            Native.Check(Rejected(() => replace!(new CancellationToken(true))) is OperationCanceledException && captures == 1,
                "Cancellation was not observed before the update reached history capture.");
            File.Delete(enabled);
            File.WriteAllText(Path.Combine(mods, "unreadable.jar"), "Incomplete inventory fixture");
            dialogue = null;
            Native.Check(Rejected(() => replace!(CancellationToken.None)) is OperationCanceledException && captures == 1 &&
                dialogue?.Contains("compixel", StringComparison.Ordinal) == true && !dialogue.Contains("already_missing", StringComparison.Ordinal),
                "An incomplete inventory must prompt for the changed declaration only and allow canceling before history capture.");
            Console.WriteLine("PASS native Mod update: new missing, disabled and out-of-range NeoForge prerequisites are blocked before replacement, with an actionable dialog. Compatible files and unrelated baseline issues preserve the update path.");
        } finally {
            PageInstanceLeft.Instance = previousInstance;
        }
    }

    private static void ModJar(string path, string id, string version, string dependencies = "")
    {
        using var jar = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(jar.CreateEntry("META-INF/neoforge.mods.toml").Open());
        writer.WriteLine($"""
            modLoader="javafml"
            loaderVersion="[4,)"
            license="MIT"
            [[mods]]
            modId="{id}"
            version="{version}"
            displayName="{id}"
            """);
        writer.WriteLine(dependencies);
    }
}
