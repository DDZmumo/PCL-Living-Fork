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
using PCLLF.Presentation.Windowing;

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
            var task=(ModLoader.LoaderTask<string,string>)Native.Call(installer,"CreateInstallTask",null,project,chosen,taskSource,target,null,false)!;
            task.Start("");task.WaitForExit();
            Native.Check(task.State==ModBase.LoadState.Finished&&File.Exists(target)&&!File.Exists(taskOld),"The actual Main post-download task did not execute its coordinated replacement.");
            Native.Check(!(bool)Native.Property(typeof(ModMinecraft.McInstanceLock),"IsAnyLocked").GetValue(null)!,"Completed mod install retained its instance write lease.");
            string failedOld=Jar(taskRoot,"failed-existing.jar","other_core","1.0");
            var failed=(ModLoader.LoaderTask<string,string>)Native.Call(installer,"CreateInstallTask",null,project,chosen,Path.Combine(root,"missing.jar"),Path.Combine(taskRoot,"missing-target.jar"),null,false)!;
            bool rejected=false;try{failed.Start("");failed.WaitForExit();}catch(Exception){rejected=true;}
            Native.Check(rejected&&File.Exists(failedOld)&&!File.Exists(Path.Combine(taskRoot,"missing-target.jar")),"A failed post-download task removed an existing mod or published a partial file.");
        } finally {LocalResourceLoaders.LocalResourceLoader.Output=before;PageInstanceLeft.Instance=previousInstance;}
        SelectedDeletion(environment);
        DragInstall(environment);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS native mod downloads: actual Main JAR parser and explicit version replacement, duplicate native rows/selection/filter and current-byte project proof; no live HTTP/game/account.");
    }
    private static void SelectedDeletion(FixtureEnvironment environment) {
        var previousOutput=LocalResourceLoaders.LocalResourceLoader.Output;var previousInstance=PageInstanceLeft.Instance;
        try {
            foreach(string[] selection in new[]{new[]{".disabled"},new[]{".old"},new[]{""},new[]{"",".disabled"}}) {
                string fixture=Path.Combine(environment.Data,"selected-mod-delete-"+Guid.NewGuid().ToString("N"));
                var owner=BackupResourceChecks.Instance(Path.Combine(fixture,"game","versions","fixture"));owner.Version=new McVersion("1.20.1",true);
                PageInstanceLeft.Instance=owner;string folder=Path.Combine(owner.PathIndie,"mods");Directory.CreateDirectory(folder);
                Native.Check(Path.GetFullPath(folder).StartsWith(Path.GetFullPath(environment.Data)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Deletion fixture escaped its owned root.");
                var entries=new List<LocalResourceFile>();var bytes=new Dictionary<string,byte[]>();
                foreach(string suffix in new[]{"",".disabled",".old"}) {
                    string path=Path.Combine(folder,"foo.jar"+suffix);
                    using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                        writer.Write(new JObject{["schemaVersion"]=1,["id"]="selected_delete_fixture",["version"]="1.0",["name"]="Fixture "+suffix}.ToString());
                    var entry=new LocalResourceFile(path);entries.Add(entry);bytes[entry.StorageKey]=File.ReadAllBytes(path);
                }
                LocalResourceLoaders.LocalResourceLoader.Output=entries;
                var snapshot=entries.ToArray();
                var page=new PageInstanceMod();object controller=Native.Get(page,"Native")!,host=Native.Get(controller,"host")!;
                Native.Set(host,"ShiftDown",new Func<bool>(()=>true));
                Native.Set(host,"ConfirmMode",new Func<string,bool>(_=>true));
                Native.Set(host,"LoaderRun",new Func<int,bool>(_=>false));
                Native.Set(host,"Background",new Action<Action,string,ThreadPriority>((action,_,_)=>action()));
                Native.Set(host,"Dispatch",new Action<Action>(action=>action()));
                string[]? inspected=null;
                var inspect=Native.Field(host.GetType(),"InspectImpacts");Type signature=inspect.FieldType;
                var args=signature.GetMethod("Invoke")!.GetParameters().Select(p=>System.Linq.Expressions.Expression.Parameter(p.ParameterType,p.Name)).ToArray();
                var capture=new Action<string[]>(paths=>inspected=paths.ToArray());
                var invoke=System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(capture),args[1]);
                var body=System.Linq.Expressions.Expression.Block(invoke,System.Linq.Expressions.Expression.New(signature.GetMethod("Invoke")!.ReturnType));
                inspect.SetValue(host,System.Linq.Expressions.Expression.Lambda(signature,body,args).Compile());
                Native.Call(controller.GetType(),"LoadUI",controller);
                var selected=selection.Select(suffix=>entries.Single(entry=>entry.File.Name=="foo.jar"+suffix)).ToArray();
                page.SelectedMods.AddRange(selected.Select(entry=>entry.StorageKey));
                Native.Call(controller.GetType(),"DeleteSelected",controller);
                // This invokes production FileUtils.Delete with Shift held: real permanent
                // deletion of GUID-owned fixtures, with no stubbed filesystem or recycle bin.
                foreach(var entry in snapshot) {
                    if(selected.Contains(entry)) Native.Check(!File.Exists(entry.File.FullName),"Selected mod was not actually deleted.");
                    else Native.Check(File.Exists(entry.File.FullName)&&File.ReadAllBytes(entry.File.FullName).SequenceEqual(bytes[entry.StorageKey]),
                        "Deleting only selected duplicates removed or changed an unselected enabled/disabled JAR.");
                }
                Native.Check(inspected is not null&&inspected.Length==selected.Length&&selected.All(entry=>inspected.Any(path=>MeloongCore.PathUtils.IsSame(path,entry.File.FullName))),
                    "Dependency inspection expanded beyond the exact selected deletion paths.");
            }
        } finally {LocalResourceLoaders.LocalResourceLoader.Output=previousOutput;PageInstanceLeft.Instance=previousInstance;}
        Console.WriteLine("PASS selected mod deletion: actual permanent delete of disabled-only, old-only, enabled-only and mixed selections; unselected bytes and exact inspection scope preserved.");
    }

    private static void DragInstall(FixtureEnvironment environment)
    {
        using var scope = new FileTaskScope(environment);
        string root = Path.Combine(scope.Root, "drag-install");
        var owner = BackupResourceChecks.Instance(Path.Combine(root, "game", "versions", "fixture"));
        owner.Version = new McVersion("1.20.1", true);
        owner.Version.ModLoaderVersions[ModLoaders.Fabric] = "0.16.1";
        string mods = Path.Combine(owner.PathIndie, "mods");
        string Jar(string name, string id) {
            string path = Path.Combine(root, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write(new JObject { ["schemaVersion"] = 1, ["id"] = id, ["version"] = "1.0.0" }.ToString());
            return path;
        }
        string incoming = Jar("drag.fixture.jar", "drag_fixture");
        string disabled = Jar("drag.second.jar.disabled", "drag_second");

        FormMain main = FileTaskShell.Install();
        PageInstanceLeft.Instance = owner;
        ModMain.FrmInstanceLeft = new PageInstanceLeft { PageID = FormMain.PageSubType.InstanceMod };
        main.PageCurrent = FormMain.PageType.InstanceSetup;
        Native.Check(main.PageCurrentSub == FormMain.PageSubType.InstanceMod, "The real WPF fixture did not select Mod management.");
        var page = new PageInstanceMod();
        object modPage = Native.Get(page, "Native")!;
        object modHost = Native.Get(modPage, "host")!;
        Task? drop = null;
        int refreshes = 0, confirmations = 0;
        bool expectConfirmation = false;
        int uiThread = Environment.CurrentManagedThreadId;
        Native.Set(modHost, "ConfirmMode", new Func<string, bool>(_ => {
            Native.Check(Environment.CurrentManagedThreadId == uiThread, "Install confirmation policy ran off-thread.");
            return true;
        }));
        var createMod = (Func<string, LocalResourceFile>)Native.Get(modHost, "NewMod")!;
        Native.Set(modHost, "NewMod", new Func<string, LocalResourceFile>(path => {
            Native.Check(Environment.CurrentManagedThreadId != uiThread, "The UI thread ran the Mod copy.");
            return createMod(path);
        }));
        Native.Set(modHost, "ForceFolderLoader", new Action<string>(folder => {
            Native.Check(Environment.CurrentManagedThreadId == uiThread && Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) == mods,
                "The post-install refresh ran off-thread or targeted a different instance.");
            refreshes++;
        }));
        Native.Set(modHost, "Message", new Func<string, string, string, string, bool, int>((body, title, _, _, _) => {
            Native.Check(expectConfirmation && Environment.CurrentManagedThreadId == uiThread && title == "Mod 安装确认" &&
                body.Contains(owner.Name, StringComparison.Ordinal), "The drop confirmation used the wrong window thread or instance.");
            confirmations++;
            return 2;
        }));
        var host = (LegacyMainWindowHost<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>)Native.Call(typeof(FormMain), "CreateWindowHost", main)!;
        host.NewThread = (work, _, _) => drop = Task.Run(work);
        host.InstallMods = files => (bool)Native.Call(modPage.GetType(), "Install", null, modHost, files, false)!;
        host.CreateCrashAnalyzer = () => throw new InvalidOperationException("The recognized Mod fell through to crash-report import.");
        var controller = new LegacyMainWindowController<FormMain.PageStackData, ModMinecraft.McInstance, ModAnimation.AniData>(new LegacyMainWindowView(), host);
        void Drop(params string[] paths) {
            controller.FileDrag(paths);
            VisualAssetChecks.Pump(() => drop?.IsCompleted == true, "The background drag handler did not return.");
            drop!.GetAwaiter().GetResult();
        }
        Drop(incoming, disabled);
        VisualAssetChecks.Pump(() => refreshes == 1, "The Mod installation did not refresh its list.");
        foreach (var (source, name) in new[] { (incoming, "drag.fixture.jar"), (disabled, "drag.second.jar") }) {
            Native.Check(File.ReadAllBytes(Path.Combine(mods, name)).SequenceEqual(File.ReadAllBytes(source)),
                "Dragged Mod bytes were not installed into the captured instance.");
        }
        expectConfirmation = true;
        ModMain.FrmInstanceLeft.PageID = FormMain.PageSubType.InstanceOverall;
        Drop(Jar("canceled.jar", "drag_canceled"));
        Native.Check(confirmations == 1 && !File.Exists(Path.Combine(mods, "canceled.jar")) && refreshes == 1,
            "Canceling a drop outside the Mod page installed a file or refreshed the loader.");
        Console.WriteLine("PASS native Mod drag: real background FileDrag, owning STA sidebar, batch byte-for-byte installation, UI refresh and cancellation.");
    }
}
