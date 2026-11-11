using System.Collections;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using PCL;

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
        LocalModSortChecks.Run(environment);
    }
}
