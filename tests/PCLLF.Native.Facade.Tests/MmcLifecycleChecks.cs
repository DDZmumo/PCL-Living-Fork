using System.Collections;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

// Original LF tests; repository LICENCE. No game, network, account or player files.
internal static class MmcLifecycleChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope tasks = new(environment);
        string root = Path.Combine(tasks.Root,"mmc-lifecycle"), game = Path.Combine(root,"minecraft");
        Directory.CreateDirectory(game); tasks.SelectMinecraft(game);
        string managed = ImportOwned(root,game);
        Native.Check(MmcInstanceInspector.Read(managed).IsConsistent,"Actual backend fixture is not a consistent managed instance.");
        var cases = new Dictionary<string,Action<string>> {
            ["source-remnant"] = path=>Put(path,"PCL/MmcSource/orphan","owned source"),
            ["payload-remnant"] = path=>Put(path,"PCL/MmcImportPayload.json","{}"),
            ["broken-definition"] = path=>Put(path,"PCL/InstanceDefinition.json","{"),
            ["unsupported"] = path=>Put(path,"PCL/InstanceDefinition.json","""{"schemaVersion":2,"managementKind":"Mmc","formatProfile":"prism-v1"}"""),
            ["pending"] = path=>Put(path,"PCL/MmcImportPending.json","{}"),
            ["unknown-observation"] = path=>Put(path,Path.GetFileName(path)+".json","{")
        };
        CheckRefused(managed);
        foreach(var pair in cases) { string path=Path.Combine(game,"versions",pair.Key); Directory.CreateDirectory(path); pair.Value(path); CheckRefused(path); }
        CheckUiAndBackground(managed);
        CheckOrdinary(root,game,managed);
        CheckManagedCloneUi(managed);
        CheckManagedRenameUi(Path.Combine(game, "versions", "managed-ui-copy"));
        InstanceConfigProviders.FlushAndDisposeWithin(root);
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS mmc-lifecycle: real managed import/prepare/repair, six damaged/residual/unknown states, dedicated managed UI clone and rename with new/retained identities, legacy clone/migration/history/update/component/delete refusal, UI/background dispatch before tasks, loose metadata/custom ordinary root/junction preservation. Guards do not claim P4 implementation.");
    }

    private static void CheckManagedRenameUi(string sourceRoot) {
        var before = MmcInstanceInspector.Read(sourceRoot);
        var type = Native.Type("PCL.MmcLifecycleUi");
        var register = Native.Property(type, "RegisterTask"); var published = Native.Property(type, "Published");
        object? oldRegister = register.GetValue(null), oldPublished = published.GetValue(null);
        var scheduler = (PCLCS.Loaders.LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)
            Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action refresh = scheduler.RefreshExtraDownload; using ManualResetEventSlim refreshed = new(false);
        ModLoader.LoaderCombo<string>? actual = null; MmcInstanceLifecycleResult? result = null;
        try {
            scheduler.RefreshExtraDownload = refreshed.Set;
            register.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(loader => { actual = loader; ModLoader.LoaderTaskbar.Add(loader); }));
            published.SetValue(null, new Action<MmcInstanceLifecycleResult>(value => result = value));
            var instance = new ModMinecraft.McInstance(sourceRoot).Load();
            using(var pause=new MmcRootUpdatePause(()=>actual)){
                Native.Static("PCL.MmcLifecycleUi","StartRename",instance,"managed-ui-canceled-rename",null);
                Native.Check(actual is not null&&pause.Captured,"Managed rename root did not reach the real Update scheduling boundary.");
                actual!.Cancel();pause.Release();pause.AwaitReturn();
                Native.Check(!(bool)Native.Property(type,"HasActiveOperation").GetValue(null)!,"Unstarted canceled rename retained its busy lease.");
                Native.Check(Native.Get(actual.Loaders.Single(),"LastRunningThread")is null&&!Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!,"managed-ui-canceled-rename")),"Unstarted rename launched a worker or wrote its target.");
                actual.Loaders.Single().Start(sourceRoot);
                Native.Check(Native.Get(actual.Loaders.Single(),"LastRunningThread")is null,"A late rename worker Start escaped its canceled one-shot fence.");
            }
            actual=null;refreshed.Reset();
            var host = new InstanceOverallPageHost<ModMinecraft.McInstance, Grid, object> {
                Instance = () => instance,
                BeginManagedRename = target => Native.Static("PCL.MmcLifecycleUi", "StartRename", target, "managed-ui-renamed", null),
                ErrorAlert = (error, _) => throw new IOException("Managed rename UI dispatch failed.", error)
            };
            new InstanceOverallPageController<ModMinecraft.McInstance, ModMinecraft.McInstance.McInstanceState,
                ModMinecraft.McInstanceCardType, Grid, object>(new(), host, _ => { }, () => 0).Rename();
            Native.Check(actual is not null, "Managed rename controller did not dispatch to the dedicated Main task.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (actual!.State == ModBase.LoadState.Loading || !refreshed.IsSet) {
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Controlled managed rename task did not finish.");
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, DateTime.UtcNow.AddSeconds(2)); Thread.Sleep(5);
            }
            Native.Check(actual.State == ModBase.LoadState.Finished && result?.Status == MmcInstanceBackendStatus.Succeeded,
                "Dedicated rename task failed: " + actual.Error);
            var renamed = MmcInstanceInspector.Read(result!.CurrentRoot!);
            Native.Check(renamed.IsConsistent && renamed.Definition!.InstanceId == before.Definition!.InstanceId &&
                renamed.Definition.Generation == before.Definition.Generation + 1 && !Directory.Exists(sourceRoot) &&
                File.Exists(Path.Combine(result.CurrentRoot!, "managed-ui-renamed.json")), "Managed UI rename lost its identity/name projection.");
        } finally { scheduler.RefreshExtraDownload = refresh; register.SetValue(null, oldRegister); published.SetValue(null, oldPublished); }
    }

    private static void CheckManagedCloneUi(string sourceRoot) {
        string before = TreeHash(sourceRoot);
        var type = Native.Type("PCL.MmcCloneUi");
        var register = Native.Property(type, "RegisterTask"); var published = Native.Property(type, "Published");
        object? oldRegister = register.GetValue(null), oldPublished = published.GetValue(null);
        var scheduler = (PCLCS.Loaders.LegacyLoaderHost<ModLoader.LoaderBase, ModBase.LoadState, MyLoading.MyLoadingState>)
            Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
        Action refresh = scheduler.RefreshExtraDownload;
        using ManualResetEventSlim refreshed = new(false);
        ModLoader.LoaderCombo<string>? actual = null; MmcInstanceCloneResult? result = null;
        try {
            // Only replace the absent window's presentation callback. The real
            // combo, state producers and backend must finish before restoration.
            scheduler.RefreshExtraDownload = refreshed.Set;
            register.SetValue(null, new Action<ModLoader.LoaderCombo<string>>(loader => { actual = loader; ModLoader.LoaderTaskbar.Add(loader); }));
            published.SetValue(null, new Action<MmcInstanceCloneResult>(value => result = value));
            var owner = new ModMinecraft.McInstance(sourceRoot).Load();
            object options = Activator.CreateInstance(Native.Type("PCL.InstanceCloneOptions"), true)!;
            using(var pause=new MmcRootUpdatePause(()=>actual)){
                Native.Static("PCL.InstanceClonerUi","Start",owner,"managed-ui-canceled-clone",options);
                Native.Check(actual is not null&&pause.Captured,"Managed clone root did not reach the real Update scheduling boundary.");
                actual!.Cancel();pause.Release();pause.AwaitReturn();
                object cloneService=Native.Field(Native.Type("PCL.InstanceClonerUi"),"Service").GetValue(null)!;
                Native.Check(!(bool)Native.Property(type,"HasActiveOperation").GetValue(null)!&&Convert.ToInt32(Native.Get(cloneService,"BusyCount"))==0,"Unstarted canceled clone retained its own or legacy busy lease.");
                Native.Check(Native.Get(actual.Loaders.Single(),"LastRunningThread")is null&&!Directory.Exists(Path.Combine(Path.GetDirectoryName(sourceRoot)!,"managed-ui-canceled-clone")),"Unstarted clone launched a worker or wrote its target.");
                actual.Loaders.Single().Start(sourceRoot);
                Native.Check(Native.Get(actual.Loaders.Single(),"LastRunningThread")is null,"A late clone worker Start escaped its canceled one-shot fence.");
            }
            actual=null;refreshed.Reset();
            Native.Static("PCL.InstanceClonerUi", "Start", owner, "managed-ui-copy", options);
            Native.Check(actual is not null, "Managed UI clone did not dispatch to the dedicated task.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (actual!.State == ModBase.LoadState.Loading || !refreshed.IsSet) {
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Controlled managed clone task did not finish.");
                Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, DateTime.UtcNow.AddSeconds(2)); Thread.Sleep(5);
            }
            Native.Call(typeof(FileTaskScope), "DrainPostedUi", null, DateTime.UtcNow.AddSeconds(2));
            Native.Check(actual.State == ModBase.LoadState.Finished && result?.Status == MmcInstanceBackendStatus.Succeeded,
                "Dedicated clone task failed: " + actual.Error);
            var source = MmcInstanceInspector.Read(sourceRoot); var clone = MmcInstanceInspector.Read(result!.Paths!.InstanceRoot);
            Native.Check(clone.IsConsistent && clone.Definition!.InstanceId != source.Definition!.InstanceId &&
                clone.Definition.ParentInstanceId == source.Definition.InstanceId && clone.Definition.Generation == 1,
                "UI clone did not publish a fresh linked MMC identity.");
            Native.Check(TreeHash(sourceRoot) == before, "Managed UI clone changed its source instance.");
        } finally { scheduler.RefreshExtraDownload = refresh; register.SetValue(null, oldRegister); published.SetValue(null, oldPublished); }
    }

    private static void CheckRefused(string path) {
        string before=TreeHash(path);
        var capability=MmcInstanceCapabilities.LegacyOperation(path,"生命周期检查");
        Native.Check(!capability.Allowed && !string.IsNullOrWhiteSpace(capability.RefusalReason),"Incomplete or managed evidence authorized a legacy writer.");
        void Refuse(Action work) {
            try { work(); }
            catch(InvalidOperationException error) when(error.Message.Contains("入口尚未适配",StringComparison.Ordinal) || error.Message.Contains("无法完整确认",StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Public lifecycle entry did not reject through the shared MMC capability gate.");
        }
        Refuse(()=>Native.Static("PCL.InstanceCloner","Clone",path,path,"blocked-copy",null,CancellationToken.None,null));
        Refuse(()=>Native.Static("PCL.InstanceCloner","PreviewMigration",path,path,CancellationToken.None));
        object migration=Activator.CreateInstance(Native.Type("PCL.InstanceCloneMigrationPlan"),true)!;
        Native.Set(migration,"SourceVersionFolder",path); Native.Set(migration,"CloneVersionFolder",path);
        Refuse(()=>Native.Static("PCL.InstanceCloner","ApplyMigration",migration,CancellationToken.None,null,null,null));
        Refuse(()=>Native.Static("PCL.EnvironmentHistory","Capture",path,path,"blocked",CancellationToken.None,null,""));
        Refuse(()=>Native.Static("PCL.EnvironmentHistory","Restore",path,path,Path.GetDirectoryName(Path.GetDirectoryName(path)!)!,"missing-point",CancellationToken.None,null,null));
        Refuse(()=>Native.Static("PCL.ModpackUpdate","Recover",path,path));
        Refuse(()=>Native.Static("PCL.ModpackUpdate","RecordInstalled","must-not-read.zip",path,null));
        Refuse(()=>Native.Static("PCL.ModpackUpdate","Commit",null,path,CancellationToken.None,null,null));
        Refuse(()=>Native.Static("PCL.ModpackUpdate","ApplyTransaction",path,path,new List<Tuple<string,string?,string?>>(),CancellationToken.None,null,null));
        Refuse(()=>Native.Static("PCL.ExportedVersionIsolation","ConfigureTarget",path,null));
        Refuse(()=>Native.Static("PCL.ExportedVersionIsolation","ConfigureExport",path,true,null));
        Refuse(()=>Native.Static("PCL.ExportedVersionIsolation","CopyOverrides","must-not-read",path,path,null));
        bool recycleCalled=false; Refuse(()=>InstanceFolderDeletion.Delete(path,true,_=>recycleCalled=true));
        Native.Check(!recycleCalled,"MMC rejection reached the recycle boundary.");
        // Metadata inspection and old snapshot listing remain available.
        _=Native.Static("PCL.EnvironmentHistory","Points",path);
        Native.Check(TreeHash(path)==before,"Rejected lifecycle operation changed original bytes or created a private task directory.");
    }

    private static void CheckUiAndBackground(string path) {
        string before=TreeHash(path);
        var instance=new ModMinecraft.McInstance(path);
        void DirectRefusal(Action action) {
            try { action(); } catch(InvalidOperationException error) when(error.Message.Contains("MMC",StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Direct component or rename entry bypassed management dispatch.");
        }
        DirectRefusal(()=>instance.Rename("blocked-rename"));
        DirectRefusal(()=>ModDownloadLib.McInstallCommit(instance,null!,path,path));
        DirectRefusal(()=>ModDownloadLib.McInstallLoadersGet(new(){InstanceFolder=path}));
        int alerts=0, writes=0, hints=0;
        void Rejected(Exception error,string _) { Native.Check(error is InvalidOperationException && error.Message.Contains("MMC",StringComparison.Ordinal),"UI rejected for an unrelated failure."); alerts++; }
        var host=new InstanceOverallPageHost<ModMinecraft.McInstance,Grid,object> { Instance=()=>instance,ModpackEnabled=()=>false,ErrorAlert=Rejected };
        var overall=new InstanceOverallPageController<ModMinecraft.McInstance,ModMinecraft.McInstance.McInstanceState,ModMinecraft.McInstanceCardType,Grid,object>(new(),host,_=>{},()=>0);
        overall.Rename(); overall.Script(); overall.Delete();
        var isolation=new ComboBox(); isolation.Items.Add("isolated"); isolation.Items.Add("global"); isolation.SelectedIndex=0;
        var settingsHost=new InstanceSettingsHost<ModMinecraft.McInstance> {
            Instance=()=>instance,Animation=()=>0,Set=(_,_,_)=>writes++,ErrorAlert=Rejected,
            Boolean=(_,_)=>true,Integer=(_,_)=>0,RefreshSettings=()=>{},Error=(error,_)=>throw new IOException("Controlled settings reload failed.",error)
        };
        var settingsView=new InstanceSettingsView<Grid,Grid>{Isolation=isolation,Login=new ComboBox(),NideControls=[null!],SetLoginLast=_=>{}};
        var settings=new InstanceSettingsController<ModMinecraft.McInstance,ModMinecraft.McInstance.McInstanceState,ModMinecraft.McInstanceCardType,Grid,Grid>(settingsView,settingsHost,null!);
        settings.Reload();
        Native.Check(!isolation.IsEnabled && isolation.ToolTip is string text && text.Contains("MMC",StringComparison.Ordinal) && ToolTipService.GetShowOnDisabled(isolation),"Disabled MMC isolation control lost its visible refusal reason.");
        settings.IsolationChanged(isolation,new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,new object[]{isolation.Items[0]},new object[]{isolation.Items[1]}));
        settings.Reset(); Native.Check(alerts==5 && writes==0,"MMC UI reached name/script/delete/isolation/reset mutation delegates.");
        var restore=new Stack<Action>();
        void Hint(object service,string field) {
            object owner=Native.Get(service,"host")!; FieldInfo member=Native.Field(owner.GetType(),field); object? previous=member.GetValue(owner); restore.Push(()=>member.SetValue(owner,previous));
            if(member.FieldType==typeof(Action<string,bool>))member.SetValue(owner,new Action<string,bool>((text,_)=>{Native.Check(text.Contains("MMC",StringComparison.Ordinal),"Missing MMC refusal reason.");hints++;}));
            else member.SetValue(owner,new Action<string>(text=>{Native.Check(text.Contains("MMC",StringComparison.Ordinal),"Missing MMC refusal reason.");hints++;}));
        }
        int tasks=ModLoader.LoaderTaskbar.Count;
        try {
            Hint(Native.Field(Native.Type("PCL.InstanceClonerUi"),"Service").GetValue(null)!,"Hint");
            Hint(Native.Field(Native.Type("PCL.EnvironmentHistoryUi"),"Service").GetValue(null)!,"HintError");
            Hint(Native.Field(Native.Type("PCL.ModpackUpdateFlow"),"Engine").GetValue(null)!,"HintRed");
            // The old workflow remains guarded; the Main UI wrapper now dispatches the supported
            // managed clone path, verified separately below through the real dedicated backend.
            object legacyClone = Native.Field(Native.Type("PCL.InstanceClonerUi"), "Service").GetValue(null)!;
            Native.Call(legacyClone.GetType(), "Start", legacyClone, instance, "blocked-copy", null);
            Native.Static("PCL.EnvironmentHistoryUi","Start",instance,true,null,null);
            Native.Static("PCL.ModpackUpdateFlow","Start",instance,true);
        } finally { while(restore.Count>0)restore.Pop()(); }
        Native.Check(hints==3 && tasks==ModLoader.LoaderTaskbar.Count,"Rejected workflows queued a task or lost their clear refusal.");
        Native.Check(TreeHash(path)==before,"UI/background or direct component guards changed managed instance bytes.");
    }

    private static void CheckOrdinary(string root,string game,string managed) {
        string ordinary=Path.Combine(game,"versions","ordinary"), custom=Path.Combine(root,"custom-ordinary");
        Directory.CreateDirectory(ordinary); Directory.CreateDirectory(custom);
        Put(ordinary,"mmc-pack.json","{loose invalid package metadata");
        Native.Check(MmcInstanceCapabilities.LegacyOperation(ordinary,"克隆").Allowed,"A loose mmc-pack.json claimed installed MMC ownership.");
        Native.Check(MmcInstanceCapabilities.LegacyOperation(custom,"更新").Allowed,"Legitimate ordinary custom roots acquired MMC's strict versions requirement.");
        Native.Static("PCL.ModpackUpdate","Recover",custom,custom);
        string payload=Path.Combine(root,"ordinary-payload"), target=Path.Combine(custom,"config","owned.cfg"); File.WriteAllText(payload,"new ordinary bytes");
        Native.Static("PCL.ModpackUpdate","ApplyTransaction",custom,custom,new List<Tuple<string,string?,string?>>{Tuple.Create(target,(string?)payload,(string?)null)},CancellationToken.None,null,null);
        Native.Check(File.ReadAllText(target)=="new ordinary bytes","Ordinary custom-root transaction was blocked or changed.");
        void Refuse(Action action) {
            try { action(); } catch(InvalidOperationException error) when(error.Message.Contains("MMC",StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("An ordinary caller or nested alias hid the actual managed owner.");
        }
        string ownedGame=Path.Combine(managed,"minecraft"), managedFile=Path.Combine(ownedGame,"config","owned.cfg");
        string nested=Path.Combine(ownedGame,"versions","ordinary-looking"); Directory.CreateDirectory(nested);
        Refuse(()=>MmcInstanceCapabilities.RequireLegacyContainingInstance(Path.Combine(nested,"file"),"嵌套路径写入"));
        string managedBefore=TreeHash(managed), ordinaryBefore=TreeHash(custom);
        Refuse(()=>Native.Static("PCL.ModpackUpdate","ApplyTransaction",custom,custom,new List<Tuple<string,string?,string?>>{Tuple.Create(managedFile,(string?)null,(string?)null)},CancellationToken.None,null,null));
        Refuse(()=>InstanceFolderDeletion.Delete(Path.GetDirectoryName(managedFile)!,false,_=>throw new IOException("Unexpected recycle boundary.")));
        Native.Check(TreeHash(managed)==managedBefore && TreeHash(custom)==ordinaryBefore,"Cross-instance target preflight wrote a transaction or changed bytes before rejecting.");
        string alias=Path.Combine(root,"ordinary-alias");
        // Junction creation below does not need symbolic-link privilege.
        using(var junction=ProcessForJunction(alias,custom)) { }
        try { Native.Check(MmcInstanceCapabilities.LegacyOperation(alias,"普通读取").Allowed,"Existing ordinary junction-root support was rejected."); }
        finally { Directory.Delete(alias,false); }
        string managedAlias=Path.Combine(root,"managed-child-alias");
        using(var junction=ProcessForJunction(managedAlias,Path.GetDirectoryName(managedFile)!)) { }
        try { Refuse(()=>InstanceFolderDeletion.Delete(managedAlias,false,_=>throw new IOException("Unexpected recycle boundary."))); }
        finally { Directory.Delete(managedAlias,false); }
        Native.Check(TreeHash(managed)==managedBefore,"A junction to a managed child bypassed the lifecycle gate.");
        string deletion=Path.Combine(root,"ordinary-delete"); Directory.CreateDirectory(deletion); Put(deletion,"owned","keep until ordinary delete");
        InstanceFolderDeletion.Delete(deletion,false,_=>throw new IOException("Unexpected recycle boundary."));
        Native.Check(!Directory.Exists(deletion),"Ordinary instance deletion changed.");
        string before=TreeHash(game);
        try { MmcInstanceCapabilities.RequireLegacyGameFolder(game,"清理PCL"); throw new Exception("Managed child did not protect whole-folder reset."); }
        catch(InvalidOperationException error) when(error.Message.Contains("MMC",StringComparison.Ordinal)) { }
        Native.Check(TreeHash(game)==before,"Whole-folder refusal changed a child.");
    }
    private static System.Diagnostics.Process ProcessForJunction(string alias,string target) {
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"/d","/c","mklink","/J",alias,target})start.ArgumentList.Add(argument);
        var process=System.Diagnostics.Process.Start(start)!; Native.Check(process.WaitForExit(5000) && process.ExitCode==0,"Owned ordinary junction prerequisite failed.");return process;
    }
    private static string ImportOwned(string root,string game) {
        string archive=Path.Combine(root,"owned.zip"), version=Path.Combine(game,"versions","managed");
        byte[] index=Encoding.UTF8.GetBytes("{\"objects\":{}}");
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)) {
            Add("mmc-pack.json","""{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
            Add("patches/net.minecraft.json",new JObject {
                ["formatVersion"]=1,["uid"]="net.minecraft",["version"]="1.7.10",["mainClass"]="fixture.Main",
                ["mainJar"]=new JObject{["name"]="fixture:game:1",["MMC-hint"]="local",["MMC-filename"]="owned.jar"},
                ["compatibleJavaMajors"]=new JArray(8),
                ["assetIndex"]=new JObject{["id"]="owned",["url"]="https://fixture.invalid/index",["sha1"]=Convert.ToHexStringLower(SHA1.HashData(index)),["size"]=index.Length}
            }.ToString());
            Add("libraries/owned.jar","Inert owned library; never executed."); Add("minecraft/config/owned.cfg","author data");
            void Add(string name,string value){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(value);}
        }
        using HttpClient client=new(new NoNetwork());
        var metadata=new MmcMetadataStore(Path.Combine(root,"metadata"),new Uri("https://fixture.invalid/metadata/"),client);
        var backend=new MmcInstanceBackend<MmcArtifactDownloadSpec>(metadata,new(Path.Combine(root,"cache","libraries"),Path.Combine(root,"cache","assets"),Path.Combine(root,"cache","sources")),spec=>spec,
            async(files,token)=>{foreach(var file in files){Native.Check(file.Urls.SequenceEqual(new[]{"https://fixture.invalid/index"}),"Unexpected fixture download.");await File.WriteAllBytesAsync(file.DownloadPath,index,token);}});
        var imported=backend.ImportAsync(new(archive,version,"managed",new("windows","x86_64",8))).GetAwaiter().GetResult();
        Native.Check(imported.Status==MmcInstanceBackendStatus.Succeeded,"Lifecycle fixture import failed: "+imported.Failure);
        var environment=new PCLLF.MMCC.Core.MmcRuntimeEnvironment("windows","x86_64",8);
        var prepared=backend.PrepareLaunchAsync(new(version,environment,imported.Definition!.InstanceId,imported.Definition.Generation)).GetAwaiter().GetResult();
        Native.Check(prepared.Status==MmcInstanceBackendStatus.Succeeded && prepared.Snapshot is not null && !prepared.Repaired,"Legacy capability guards blocked dedicated MMC launch preparation: "+prepared.Failure);
        var repaired=backend.RepairAsync(new(version,environment,imported.Definition.InstanceId){ExpectedGeneration=imported.Definition.Generation}).GetAwaiter().GetResult();
        Native.Check(repaired.Status==MmcInstanceBackendStatus.Succeeded,"Legacy capability guards blocked dedicated MMC repair: "+repaired.Failure);
        return version;
    }
    private sealed class NoNetwork:HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new IOException("Lifecycle fixtures cannot access network."); }
    private static void Put(string root,string relative,string value){string path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,value,new UTF8Encoding(false));}
    private static string TreeHash(string root)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(path=>path,StringComparer.Ordinal).Select(path=>Path.GetRelativePath(root,path)+":"+Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))))));
}
