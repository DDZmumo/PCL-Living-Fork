// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class NativeModDownloadChecks {
    internal static void Run(FixtureEnvironment environment) {
        string root=Path.Combine(environment.Data,"mod-download-"+Guid.NewGuid().ToString("N")),mods=Path.Combine(root,"mods");Directory.CreateDirectory(mods);
        string Jar(string folder,string name,string id,string version) {
            Directory.CreateDirectory(folder);string path=Path.Combine(folder,name);
            using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
            using var writer=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write(new JObject{["schemaVersion"]=1,["id"]=id,["name"]=id,["version"]=version}.ToString());return path;
        }
        Type inspector=Native.Type("PCL.ModDependencyInspector"),installer=Native.Type("PCL.ModDownloadInstaller");
        object context=Activator.CreateInstance(Native.Type("PCL.ModDependencyInspector+InspectionContext"),true)!;
        Native.Set(context,"MinecraftVersion","1.20.1");Native.Set(context,"LoaderKnown",true);
        ((IDictionary)Native.Get(context,"LoaderVersions")!).Add("fabricloader","0.16.0");
        string old=Jar(mods,"old.jar","fixture_core","9.0"),extra=Jar(mods,"other.jar","unrelated","1.0"),incoming=Jar(root,"new.jar","fixture_core","2.0");
        var result=new ModFileInstall((path,token)=>(IReadOnlyCollection<string>?)Native.Call(installer,"ReadIds",null,path,context,token))
            .Install(incoming,Path.Combine(mods,"chosen.jar"));
        Native.Check(result.ReplacedFiles==1&&!File.Exists(old)&&File.Exists(extra)&&File.Exists(result.Destination),"Actual Main archive identities did not replace the explicit version or removed another mod.");
        var before=LocalResourceLoaders.LocalResourceLoader.Output;var previousInstance=PageInstanceLeft.Instance;
        try {
            var owner=BackupResourceChecks.Instance(Path.Combine(root,"game","versions","fixture"));
            owner.Version=new McVersion("1.20.1",true);PageInstanceLeft.Instance=owner;
            string disabled=result.Destination+".disabled";File.Copy(result.Destination,disabled);
            var first=new LocalResourceFile(result.Destination){DeclaredModIds=new[]{"fixture_core"}};
            var second=new LocalResourceFile(disabled){DeclaredModIds=new[]{"fixture_core"}};
            var other=new LocalResourceFile(extra){DeclaredModIds=new[]{"unrelated"}};
            LocalResourceLoaders.LocalResourceLoader.Output=new(){first,second,other};
            var page=new PageInstanceMod();object controller=Native.Get(page,"Native")!;Native.Call(controller.GetType(),"LoadUI",controller);
            Native.Check(page.ModItems.Count==3&&((Panel)page.FindName("PanList")).Children.Count==3,"Enabled and disabled files with the same basename collided in the real page row dictionary.");
            var item=page.ModItems[first.StorageKey];item.Checked=true;page.CheckChanged(item,new ModBase.RouteEventArgs());
            Native.Check(page.SelectedMods.Count==1&&page.SelectedMods[0]==first.StorageKey,"Selecting one duplicate also selected its disabled sibling.");
            var filter=(MyRadioButton)page.FindName("BtnFilterDuplicate");Native.Call(typeof(PageInstanceMod),"ChangeFilter",page,filter,true);
            Native.Check(((Panel)page.FindName("PanList")).Children.Count==2,"Actual native duplicate filter missed root-ID conflicts.");
            var project=new ResourceProject(new JObject{["Platform"]=(int)ResourcePlatforms.Modrinth,["Types"]=(int)ResourceTypes.Mod,["Id"]="fixture-project",["Slug"]="fixture-project",["RawName"]="Fixture",["Description"]="Fixture summary",["Website"]="https://example.invalid",["LastUpdate"]=DateTime.UtcNow,["DownloadCount"]=0,["Tags"]=new JArray()});
            var version=(ResourceVersion)Activator.CreateInstance(typeof(ResourceVersion))!;
            version.Hash=Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(first.File.FullName))).ToLowerInvariant();
            first.Project=project;first.ProjectVersion=version;
            Native.Check((bool)Native.Call(installer,"MatchesVerifiedProject",null,first.File.FullName,project,CancellationToken.None)!,"Exact cached platform match was not checked against current file bytes.");
            File.AppendAllText(first.File.FullName,"changed");
            Native.Check(!(bool)Native.Call(installer,"MatchesVerifiedProject",null,first.File.FullName,project,CancellationToken.None)!,"A stale project/version cache authorized replacement after its file bytes changed.");
            string taskRoot=Path.Combine(root,"task-mods");
            string taskOld=Jar(taskRoot,"previous.jar","task_core","9.0"),taskSource=Jar(root,"task-source.jar","task_core","1.0");
            var chosen=(ResourceVersion)Activator.CreateInstance(typeof(ResourceVersion))!;
            chosen.GameVersions=new(){"1.20.1"};chosen.ModLoaders=ModLoaders.Fabric;
            string target=Path.Combine(taskRoot,"selected.jar");
            var task=(ModLoader.LoaderTask<string,string>)Native.Call(installer,"CreateInstallTask",null,project,chosen,taskSource,target,null)!;
            task.Start("");task.WaitForExit();
            Native.Check(task.State==ModBase.LoadState.Finished&&File.Exists(target)&&!File.Exists(taskOld),"The actual Main post-download task did not execute its coordinated replacement.");
            Native.Check(!(bool)Native.Property(typeof(ModMinecraft.McInstanceLock),"IsAnyLocked").GetValue(null)!,"Completed mod install retained its instance write lease.");
            string failedOld=Jar(taskRoot,"failed-existing.jar","other_core","1.0");
            var failed=(ModLoader.LoaderTask<string,string>)Native.Call(installer,"CreateInstallTask",null,project,chosen,Path.Combine(root,"missing.jar"),Path.Combine(taskRoot,"missing-target.jar"),null)!;
            bool rejected=false;try{failed.Start("");failed.WaitForExit();}catch(Exception){rejected=true;}
            Native.Check(rejected&&File.Exists(failedOld)&&!File.Exists(Path.Combine(taskRoot,"missing-target.jar")),"A failed post-download task removed an existing mod or published a partial file.");
        } finally {LocalResourceLoaders.LocalResourceLoader.Output=before;PageInstanceLeft.Instance=previousInstance;}
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native mod downloads: actual Main JAR parser and explicit version replacement, duplicate native rows/selection/filter and current-byte project proof; no live HTTP/game/account.");
    }
}
