// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
using Newtonsoft.Json.Linq;
using PCLCS;
using Context = ModDependencyInspectorPipelineTests.Context;
using Local = PCLCS.ModDependencyInspection<ModDependencyInspectorPipelineTests.Context, ModDependencyInspectorPipelineTests.Mod, ModDependencyInspectorPipelineTests.Dependency, ModDependencyInspectorPipelineTests.Edge, ModDependencyInspectorPipelineTests.Issue, ModDependencyInspectorPipelineTests.Impact, ModDependencyInspectorPipelineTests.Report, ModDependencyInspectorPipelineTests.OnlineProject, ModDependencyInspectorPipelineTests.OnlineDependency, ModDependencyInspectorPipelineTests.OnlineFile>;

internal static partial class ModDependencyOnlinePipelineTests {
    private static void QuiltVersionDefaults() {
        foreach(var v in new (string Actual,string Range,bool Fits)[]{("1.5.0","1.0.0",true),("0.9.0","0.2.1",true),("0.9.0","^0.2.1",true),("1.1.0","~1",false),("1.5.0","=1.0.0",false)})
            Equal(v.Fits?ModDependencyMatchState.Satisfied:ModDependencyMatchState.Unsatisfied,Local.MatchVersion(v.Actual,v.Range,"quilt"));
        Equal(ModDependencyMatchState.Unsatisfied,Local.MatchVersion("1.5.0","1.0.0","fabric"));
        using Fixture f=new();string folder=f.Folder("quilt-version-default");
        var descriptor=new JObject{["schema_version"]=1,["quilt_loader"]=new JObject{["group"]="fixture",["id"]="consumer",["version"]="1.0",["depends"]=new JArray(new JObject{["id"]="library",["versions"]="1.0.0"})}};
        DescriptorJar(folder,"consumer.jar","quilt.mod.json",descriptor.ToString());
        DescriptorJar(folder,"library.jar","fabric.mod.json",FabricDescriptor("library","1.5.0").ToString());
        Context c=new(){MinecraftVersion="1.21.1",JavaVersion="21",LoaderKnown=true};c.LoaderVersions["quilt_loader"]="0.28.0";
        var r=Local.Inspect(folder,c);True(!r.HasErrors,"Quilt bare version was evaluated as Fabric equality.");Equal("quilt",r.Edges.Single().Requirement.Syntax);
        True(r.IndexPersisted,"Quilt graph was not cached.");Local.Index.ClearMemory();
        string cache=Directory.GetFiles(Local.Index.CacheDirectory,"*.json",SearchOption.AllDirectories).Single();
        var old=JObject.Parse(File.ReadAllText(cache));old["Version"]=4;
        foreach(var syntax in old.SelectTokens("$..Syntax").ToArray())syntax.Replace(new JValue("fabric"));
        File.WriteAllText(cache,old.ToString());
        var rebuilt=Local.Inspect(folder,c);True(!rebuilt.IndexHit&&!rebuilt.HasErrors&&rebuilt.Edges.Single().Requirement.Syntax=="quilt","A pre-rule descriptor cache retained Fabric syntax for Quilt.");
    }
    private static void JavaFeatureDeclarations() {
        foreach(var v in new (string Loader,string Key,string Java,bool Enforced,bool Fits)[]{
            ("neoforge","javaVersion","17",true,false),("neoforge","javaVersion","21.0.2",true,true),
            ("forge","java_version","17",true,false),("forge","java_version","21",true,true),
            ("forge","javaVersion","17",false,true)}) {
            using Fixture f=new();string folder=f.Folder("java-feature");
            string text=$"modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\n[[mods]]\nmodId=\"consumer\"\nversion=\"1.0\"\n[features.consumer]\n{v.Key}=\"[21,)\"\n";
            DescriptorJar(folder,"consumer.jar",v.Loader=="neoforge"?"META-INF/neoforge.mods.toml":"META-INF/mods.toml",text);
            Context c=new(){MinecraftVersion="1.21.1",JavaVersion=v.Java,LoaderKnown=true};c.LoaderVersions[v.Loader]="21.1.265";c.AdditionalBuiltins["javafml"]="4.0.45";
            var r=Local.Inspect(folder,c);Equal(v.Enforced?1:0,r.Edges.Count(e=>e.Requirement.Id=="java"));Equal(!v.Fits,r.HasErrors);
        }
    }
    private static void MavenKnownPrereleases() {
        // Results checked against Maven Artifact 3.9.9, not SemVer assumptions.
        foreach(var v in new (string Actual,string Range,bool Fits)[]{
            ("21.7.26-beta","[21.7,21.8)",true),("21.7.26-beta","[21.7.26,)",false),("21.7.26-beta","(,21.7.26)",true),
            ("2.0-rc1","[1,2)",true),("2.0-SNAPSHOT","[2,)",false),("1.0.1-alpha.2","[1,2)",true),
            ("1.0-beta","[1.0-beta]",true),("1.0-RC1","[1.0-rc1]",true)})
            Equal(v.Fits?ModDependencyMatchState.Satisfied:ModDependencyMatchState.Unsatisfied,Local.MatchVersion(v.Actual,v.Range,"maven"));
        foreach(string value in new[]{"1.0-final","1.0-custom","1.0-sp","${version}"})
            Equal(ModDependencyMatchState.Unknown,Local.MatchVersion(value,"[1,2)","maven"));
        Equal(ModDependencyMatchState.Unknown,Local.MatchVersion("1.0-beta.1","[1.0-beta.2,2)","maven"));
    }
    private static void NeoForge1218Matrix() {
        foreach(var v in new (string Game,string Id,string Range,bool Fits)[]{
            ("1.21.8","minecraft","[1.21.7]",true),("1.21.8","neoforge","[21.7,21.8)",true),
            ("1.21.8","neoforge","[21.7.26-beta]",true),("1.21.8","neoforge","[21.7.26]",false),
            ("1.21.8","neoforge","[21.7.27,21.8)",false),("1.21.9","minecraft","[1.21.7]",false)}) {
            using Fixture f=new();string folder=f.Folder("neo-1218");
            string text=$"modLoader=\"javafml\"\nloaderVersion=\"[4,)\"\n[[mods]]\nmodId=\"consumer\"\nversion=\"1.0\"\n[[dependencies.consumer]]\nmodId=\"{v.Id}\"\ntype=\"required\"\nversionRange=\"{v.Range}\"\n";
            DescriptorJar(folder,"consumer.jar","META-INF/neoforge.mods.toml",text);
            Context c=new(){MinecraftVersion=v.Game,JavaVersion="21",LoaderKnown=true};c.LoaderVersions["neoforge"]="21.8.47";c.AdditionalBuiltins["javafml"]="4.0.45";
            var r=Local.Inspect(folder,c);Equal(v.Fits?ModDependencyMatchState.Satisfied:ModDependencyMatchState.Unsatisfied,r.Edges.Single(e=>e.Requirement.Id==v.Id).StateValue);Equal(!v.Fits,r.HasErrors);
        }
    }
}
