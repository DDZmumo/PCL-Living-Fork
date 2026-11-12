// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using ExportPipeline = PCLCS.ModpackExportPipeline<PCL.ModMinecraft.McInstance, PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion>;
using ExportHost = PCLCS.ModpackExportHost<PCL.ModMinecraft.McInstance, PCL.LocalResourceFile, PCL.ResourceProject, PCL.ResourceVersion>;

namespace PCLLF.Native.Facade.Tests;

internal static class ModpackExportChecks {
    internal static void Run(FixtureEnvironment environment) {
        foreach (bool readOnly in new[] { false, true })
            foreach (bool hosted in new[] { true, false })
                foreach (bool launcher in new[] { false, true }) RunCase(environment, hosted, launcher, readOnly);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native modpack export (8 combinations): actual Copy/Online/Archive handle readonly hosted mods/packs and unmatched overrides, preserve every source byte/attribute, validate manifest hashes/URLs/paths and both direct/nested packages; no network or Explorer launched.");
    }

    private static void RunCase(FixtureEnvironment environment, bool hosted, bool launcher, bool readOnly) {
        string root=Path.Combine(environment.Data,$"readonly-export-{hosted}-{launcher}-{readOnly}");
        Native.Check(!Directory.Exists(root),"Refusing an existing export fixture.");
        string game=Path.GetFullPath(Path.Combine(root,"minecraft/versions/example"))+Path.DirectorySeparatorChar;
        string cache=Path.Combine(root,"staging")+Path.DirectorySeparatorChar;
        string overrides=Path.GetFullPath(Path.Combine(cache,"modpack/overrides"))+Path.DirectorySeparatorChar;
        var instance=BackupResourceChecks.Instance(game.TrimEnd(Path.DirectorySeparatorChar));
        var originals=new Dictionary<string,(byte[] Bytes,FileAttributes Attributes)>();
        foreach (string relative in new[] {"mods/matched.jar","resourcepacks/matched.zip","mods/unmatched.jar","config/readonly.cfg","mods/plain.jar"}) {
            string source=Path.Combine(game,relative);Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source,Encoding.UTF8.GetBytes("Synthetic export file: "+relative));
            if (readOnly&&!relative.EndsWith("plain.jar",StringComparison.Ordinal)) File.SetAttributes(source,File.GetAttributes(source)|FileAttributes.ReadOnly);
            originals.Add(relative,(File.ReadAllBytes(source),File.GetAttributes(source)));
        }
        string executable=Path.Combine(root,"fixture-launcher.exe");
        File.WriteAllText(executable,"Synthetic launcher payload, never executed.");
        var request=new ModpackExportRequest<ModMinecraft.McInstance> {
            Instance=instance,GameRoot=game,CacheFolder=cache,OverridesFolder=overrides,
            Name="Readonly export fixture",Version="1.0",LoaderName="Isolated export",
            PackPath=Path.Combine(root,"result.zip"),Rules=new List<string>{"*"},ExtraFiles=new(),
            CheckHostedAssets=hosted,IncludeLauncher=launcher
        };
        try { RunExport(); }
        finally {
            // Only these synthetic, GUID-owned files receive cleanup changes,
            // after the assertions that export preserved source attributes.
            foreach(string relative in originals.Keys)
                foreach(string path in new[]{Path.Combine(game,relative),Path.Combine(overrides,relative)})
                    if(File.Exists(path)) File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);
        }

        void RunExport() {
            List<LocalResourceFile> candidates=new();Dictionary<LocalResourceFile,List<string>> matches=new();
            int requests=0,explorer=0;double progress=0;
            var host=new ExportHost {
                VersionFolder=_=>game,Version=_=>new McVersion("1.21.1",true),Info=_=>"Synthetic pack",
                SelectedMinecraftFolder=()=>Path.Combine(root,"minecraft"),CreateLocal=path=>new LocalResourceFile(path),
                Executable=()=>executable,ExecutableName=()=>Path.GetFileName(executable),
                WriteBoolIni=(path,_,_)=>{Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"IsStar:False");},
                ConfigureIsolation=(_,_,_)=>{},ConfigureMode=(_,_,_)=>{},InfoLog=_=>{},
                Explorer=path=>{Native.Check(path==request.PackPath,"Explorer received a foreign path.");explorer++;},
                StartThread=(work,_)=>{work();return new Thread(()=>{});},
                Request=(url,_,_,_)=>{
                    requests++;
                    if(url=="https://api.curseforge.com/v1/fingerprints/432")return new JObject{["data"]=new JObject{["exactMatches"]=new JArray()}};
                    Native.Check(url=="https://api.modrinth.com/v2/version_files","Unexpected export network endpoint.");
                    var response=new JObject();
                    foreach(var file in candidates.Where(file=>file.File.Name.StartsWith("matched.",StringComparison.Ordinal)))
                        response[file.ModrinthHash]=new JObject{["files"]=new JArray(new JObject{["hashes"]=new JObject{["sha1"]=file.ModrinthHash},["url"]="https://fixture.invalid/"+file.File.Name})};
                    return response;
                },
                Confirm=(_,_,_,_)=>throw new InvalidOperationException("Unexpected export prompt."),
                Warn=(error,message)=>throw new InvalidOperationException(message,error)
            };
            var pipeline=new ExportPipeline(host);
            pipeline.Copy(request,new ModpackExportCopyTask<LocalResourceFile> {
                Output=()=>candidates,SetOutput=files=>candidates=files,Progress=()=>progress,SetProgress=value=>progress=value,
                Cancellation=()=>CancellationToken.None,ProgressProvider=(_,_)=>throw new InvalidOperationException("Java was not selected.")
            });
            foreach(var original in originals.Where(pair=>pair.Value.Attributes.HasFlag(FileAttributes.ReadOnly)))
                Native.Check(File.GetAttributes(Path.Combine(overrides,original.Key)).HasFlag(FileAttributes.ReadOnly),"The fixture did not reproduce copied readonly attributes.");
            pipeline.Online(request,new ModpackExportOnlineTask<LocalResourceFile> {
                Input=()=>candidates,Output=()=>matches,SetOutput=files=>matches=files,
                IsCanceled=()=>false,Progress=()=>progress,SetProgress=value=>progress=value
            });
            Native.Check(requests==(hosted?2:0)&&matches.Count==(hosted?2:0),"Hosted lookup or offline export selection changed.");
            try {
                pipeline.Archive(request,new ModpackExportArchiveTask<LocalResourceFile>{Input=()=>matches,SetProgress=value=>progress=value});
                using var outer=ZipFile.OpenRead(request.PackPath);
                if(launcher) {
                    Native.Check(outer.GetEntry("fixture-launcher.exe") is not null,"Launcher package lost its payload.");
                    using var inner=outer.GetEntry("modpack.mrpack")!.Open();using var bytes=new MemoryStream();inner.CopyTo(bytes);bytes.Position=0;
                    using var pack=new ZipArchive(bytes,ZipArchiveMode.Read);ValidatePack(pack);
                } else ValidatePack(outer);
                Native.Check(explorer==1,"Successful export did not complete exactly once.");
            } finally {
                foreach(var original in originals) {
                    string source=Path.Combine(game,original.Key);
                    Native.Check(File.ReadAllBytes(source).SequenceEqual(original.Value.Bytes)&&File.GetAttributes(source)==original.Value.Attributes,
                        "Export modified original game bytes or attributes: "+original.Key);
                }
            }

            void ValidatePack(ZipArchive pack) {
                using var text=new StreamReader(pack.GetEntry("modrinth.index.json")!.Open());
                var manifest=JObject.Parse(text.ReadToEnd());var files=(JArray)manifest["files"]!;
                Native.Check(files.Count==(hosted?2:0),"Export manifest lost hosted resource entries.");
                foreach(var original in originals) {
                    bool matched=hosted&&Path.GetFileName(original.Key).StartsWith("matched.",StringComparison.Ordinal);
                    var entry=pack.GetEntry("overrides/"+original.Key);
                    if(matched) {
                        Native.Check(entry is null,"Hosted readonly resource was also bundled in overrides.");
                        var file=files.OfType<JObject>().Single(file=>(string?)file["path"]==original.Key);
                        Native.Check((long?)file["fileSize"]==original.Value.Bytes.Length&&
                            (string?)file["hashes"]?["sha1"]==Convert.ToHexStringLower(SHA1.HashData(original.Value.Bytes))&&
                            (string?)file["hashes"]?["sha512"]==Convert.ToHexStringLower(SHA512.HashData(original.Value.Bytes))&&
                            (string?)file["downloads"]?[0]=="https://fixture.invalid/"+Path.GetFileName(original.Key),"Exported hosted metadata changed.");
                    } else {
                        Native.Check(entry is not null,"Unmatched readonly override was dropped.");
                        using var content=entry!.Open();using var bytes=new MemoryStream();content.CopyTo(bytes);
                        Native.Check(bytes.ToArray().SequenceEqual(original.Value.Bytes),"Override content changed.");
                    }
                }
            }
        }
    }
}
