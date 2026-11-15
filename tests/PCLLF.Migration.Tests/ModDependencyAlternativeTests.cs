// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using PCLCS;
using Context = ModDependencyInspectorPipelineTests.Context;
using Local = PCLCS.ModDependencyInspection<ModDependencyInspectorPipelineTests.Context, ModDependencyInspectorPipelineTests.Mod, ModDependencyInspectorPipelineTests.Dependency, ModDependencyInspectorPipelineTests.Edge, ModDependencyInspectorPipelineTests.Issue, ModDependencyInspectorPipelineTests.Impact, ModDependencyInspectorPipelineTests.Report, ModDependencyInspectorPipelineTests.OnlineProject, ModDependencyInspectorPipelineTests.OnlineDependency, ModDependencyInspectorPipelineTests.OnlineFile>;

internal static partial class ModDependencyOnlinePipelineTests {
    private static string DescriptorJar(string folder,string name,string descriptor,string content) {
        string path=Path.Combine(folder,name);using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        using var writer=new StreamWriter(archive.CreateEntry(descriptor).Open(),new UTF8Encoding(false));writer.Write(content);return path;
    }
    private static JObject FabricDescriptor(string id,string version="2.0")=>new(){["schemaVersion"]=1,["id"]=id,["version"]=version};
    private static void NeoForgeHotfixCompatibility() {
        foreach(var test in new (string Game,string Loader,string Id,string Range,bool Known,bool Fits)[]{
            (Game:"1.21.1",Loader:"neoforge",Id:"minecraft",Range:"[1.21]",Known:true,Fits:true),
            ("1.21.1","neoforge","minecraft","[1.21,1.21.1)",true,true),
            ("1.21.1","neoforge","neoforge","[21.0,21.1)",true,true),
            ("1.21.1","neoforge","minecraft","[1.21.1,)",true,true),
            ("1.21.1","neoforge","minecraft","[1.20.4]",true,false),
            ("1.21.4","neoforge","minecraft","[1.21]",true,false),
            ("1.21.1","forge","minecraft","[1.21]",true,false),
            ("1.21.1","neoforge","minecraft","[1.21]",false,false)}) {
            using Fixture f=new();string folder=f.Folder("hotfix");
            string toml=$"modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\n[[mods]]\nmodId=\"consumer\"\nversion=\"1.0\"\n[[dependencies.consumer]]\nmodId=\"{test.Id}\"\ntype=\"required\"\nmandatory=true\nversionRange=\"{test.Range}\"\n";
            DescriptorJar(folder,"consumer.jar",test.Loader=="neoforge"?"META-INF/neoforge.mods.toml":"META-INF/mods.toml",toml);
            Context context=new(){MinecraftVersion=test.Game,JavaVersion="21",LoaderKnown=test.Known};
            context.LoaderVersions[test.Loader]=test.Loader=="neoforge"?"21.1.265":"51.0.0";context.AdditionalBuiltins["javafml"]="4.0.45";
            var report=Local.Inspect(folder,context);
            var edge=report.Edges.Single(e=>e.Requirement.Id==test.Id);
            Equal(test.Fits?ModDependencyMatchState.Satisfied:ModDependencyMatchState.Unsatisfied,edge.StateValue);
            Equal(!test.Fits,report.HasErrors);
        }
        Equal(ModDependencyMatchState.Unsatisfied,Local.MatchVersion("1.21.1","[1.21]","maven"));
        Equal(ModDependencyMatchState.Unsatisfied,Local.MatchVersion("21.1.265","[21.0,21.1)","maven"));
    }
    private static void ReplacementPlatformDependencies() {
        foreach(string mode in new[]{"required","recommended","suggested","disabled","old-version","wrong-reference"}) {
            using Fixture f=new();string folder=f.Folder("replacement-"+mode);
            JObject owner=FabricDescriptor("consumer");owner[mode=="recommended"?"recommends":mode=="suggested"?"suggests":"depends"]=new JObject{["primary_api"]=">=1 <3"};
            JObject peer=FabricDescriptor("replacement_api",mode=="old-version"?"0.5":"2.0");peer["provides"]=new JArray("primary_api");
            string ownerPath=DescriptorJar(folder,"consumer.jar","fabric.mod.json",owner.ToString());
            string peerPath=DescriptorJar(folder,"replacement.jar"+(mode=="disabled"?".disabled":""),"fabric.mod.json",peer.ToString());
            string reference=f.Jar(f.Folder("reference"),"primary.jar",mode=="wrong-reference"?"unrelated_api":"primary_api");
            var report=EnrichAlternative(f,folder,ownerPath,peerPath,reference,"fabricloader");
            bool fits=mode is "required" or "recommended" or "suggested";
            Equal(fits,report.PlatformReviewNotes().Count==0);
            Equal(mode is "disabled" or "old-version",report.HasErrors);
        }
    }
    private static void AlternativePlatformDependencies() {
        foreach(bool any in new[]{true,false}) {
            using Fixture f=new();string folder=f.Folder("quilt-alternative");
            JArray leaves=new("primary_api","alternate_api");
            JObject owner=new(){["schema_version"]=1,["quilt_loader"]=new JObject{["group"]="fixture",["id"]="consumer",["version"]="1.0",["depends"]=any?new JArray((JToken)leaves):leaves}};
            string ownerPath=DescriptorJar(folder,"consumer.jar","quilt.mod.json",owner.ToString());
            string peerPath=DescriptorJar(folder,"alternate.jar","fabric.mod.json",FabricDescriptor("alternate_api").ToString());
            string reference=f.Jar(f.Folder("reference"),"primary.jar","primary_api");
            var report=EnrichAlternative(f,folder,ownerPath,peerPath,reference,"quilt_loader");
            Equal(any,report.PlatformReviewNotes().Count==0);Equal(!any,report.HasErrors);
        }
    }
    private static ModDependencyInspectorPipelineTests.Report EnrichAlternative(Fixture fixture,string folder,string owner,string peer,string reference,string loader) {
        Context context=new(){MinecraftVersion="1.21.1",JavaVersion="21",LoaderKnown=true};context.LoaderVersions[loader]="0.28.0";
        Network network=new();
        network.Versions=new JObject{[Sha1(owner)]=MrVersion(owner,"ParentP1","OwnerV01",new JArray(new JObject{["project_id"]="PrimaryP",["version_id"]="RefV0001",["dependency_type"]="required"})),[Sha1(peer)]=MrVersion(peer,"OtherPrj","PeerVer1")};
        JObject referenceVersion=MrVersion(reference,"PrimaryP","RefV0001");referenceVersion["files"]![0]!["url"]="https://cdn.modrinth.com/data/PrimaryP/versions/RefV0001/reference.jar";
        network.VersionProjects=new JArray(referenceVersion);network.Projects=new JArray(Project("ParentP1"),Project("OtherPrj"),Project("PrimaryP"));
        Options options=network.CreateOptions();options.ComparePlatformDependencies=true;int archiveReads=0;
        options.ArchiveRequest=(url,size,token)=>{token.ThrowIfCancellationRequested();archiveReads++;True(size==new FileInfo(reference).Length&&ModDependencyTransport.IsAllowedArchive(url),"Unbounded reference read.");return File.ReadAllBytes(reference);};
        var before=Snapshot(folder);var report=Engine(network).Enrich(Local.Inspect(folder,context),folder,options:options);
        SameSnapshot(before,folder);True(archiveReads<=1,"Redundant metadata archive requests.");network.Healthy();return report;
    }
    private static void BoundedReplacementVerification() {
        foreach(bool replacement in new[]{true,false}) {
            using Fixture f=new();string folder=f.Folder("bounded-identities"),references=f.Folder("references");
            Network network=new();var archiveBytes=new Dictionary<string,byte[]>();var owners=new List<string>();
            for(int i=0;i<4;i++) {
                string id="library_"+i;JObject owner=FabricDescriptor("consumer_"+i);owner["depends"]=new JObject{[id]="*"};
                JObject peer=FabricDescriptor(replacement&&i==3?"replacement_api":id);
                if(replacement&&i==3)peer["provides"]=new JArray(id);
                string ownerPath=DescriptorJar(folder,$"owner-{i}.jar","fabric.mod.json",owner.ToString());owners.Add(ownerPath);
                string peerPath=DescriptorJar(folder,$"peer-{i}.jar","fabric.mod.json",peer.ToString());
                string reference=f.Jar(references,$"reference-{i}.jar",id);
                string project="NeedP00"+i,version="RefV000"+i,url=$"https://cdn.modrinth.com/data/{project}/versions/{version}/reference.jar";
                network.Versions[Sha1(ownerPath)]=MrVersion(ownerPath,"OwnerP0"+i,"OwnerV0"+i,new JArray(new JObject{["project_id"]=project,["version_id"]=version,["dependency_type"]="required"}));
                network.Versions[Sha1(peerPath)]=MrVersion(peerPath,"OtherP0"+i,"PeerV00"+i);
                var referenceVersion=MrVersion(reference,project,version);referenceVersion["files"]![0]!["url"]=url;
                network.VersionProjects.Add(referenceVersion);archiveBytes[url]=File.ReadAllBytes(reference);
                foreach(string projectId in new[]{project,"OwnerP0"+i,"OtherP0"+i})network.Projects.Add(Project(projectId));
            }
            if(!replacement)network.Cache.Write("loader-ids|Modrinth|NeedP003|RefV0003|1.21.1|fabricloader",new JArray("library_3"),network.Now,TimeSpan.FromHours(1));
            Context context=new(){MinecraftVersion="1.21.1",JavaVersion="21",LoaderKnown=true};context.LoaderVersions["fabricloader"]="0.16.0";
            Options options=network.CreateOptions();options.ComparePlatformDependencies=true;options.MaxRequests=20;int reads=0;
            options.ArchiveRequest=(url,size,token)=>{token.ThrowIfCancellationRequested();reads++;True(archiveBytes.ContainsKey(url)&&archiveBytes[url].LongLength==size,"Unexpected bounded archive request.");return archiveBytes[url];};
            var report=Engine(network).Enrich(Local.Inspect(folder,context),folder,options:options);
            var requirement=OnlineFileInfo(report,owners[3]).Matches.Single().Dependencies.Single();
            Equal(1,Local.RuntimeProvidersForPlatformDependency(report,owners[3],requirement).Count);
            True(!report.PlatformReviewNotes().Any(note=>note.StartsWith("owner-3.jar",StringComparison.Ordinal)),"An installed replacement or cached provider was lost behind the archive budget.");
            True(!report.HasErrors&&reads==2,"The archive bound changed or local declarations were damaged.");network.Healthy();
        }
    }
    private static void RealReplacementDeclarations(string input) {
        var sources=new Dictionary<string,string>{
            ["insecticide.jar"]="70511e9bcb3a6aa54a31b008ef88160c62a1305d",
            ["consumer.jar"]="4ddb38099c466f663ce97e4bfcc741a53e0cb48e",
            ["cicada-reference.jar"]="b592d6a5131034120703d03ec36054202eb51f53"};
        foreach(var pair in sources)Equal(pair.Value,Sha1(Path.Combine(input,pair.Key)));
        using Fixture f=new();string folder=f.Folder("actual-metadata");
        string owner=Path.Combine(folder,"consumer.jar"),peer=Path.Combine(folder,"insecticide.jar");
        File.Copy(Path.Combine(input,"consumer.jar"),owner);File.Copy(Path.Combine(input,"insecticide.jar"),peer);
        // Isolate this library relationship: no Minecraft or mod code executes.
        DescriptorJar(folder,"fabric-api-stub.jar","fabric.mod.json",FabricDescriptor("fabric").ToString());
        var report=EnrichAlternative(f,folder,owner,peer,Path.Combine(input,"cicada-reference.jar"),"fabricloader");
        True(!report.HasErrors&&report.PlatformReviewNotes().Count==0,"Actual replacement declarations still triggered a missing-library prompt.");
        True(report.Edges.Any(edge=>edge.Requirement.Id=="cicada"&&edge.StateValue==ModDependencyMatchState.Satisfied&&edge.Providers.Any(provider=>provider.Id=="insecticide")),"Actual provides/version proof was lost.");
        foreach(var pair in sources)Equal(pair.Value,Sha1(Path.Combine(input,pair.Key)));
    }
}
