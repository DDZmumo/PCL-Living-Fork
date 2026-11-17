using System.Collections.Immutable;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;
using SetupController = PCLCS.InstanceSettingsController<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.MyListItem, PCL.MyIconButton>;

namespace PCLLF.Native.Facade.Tests;

// Real Main settings controls/worker and installed PE Java probes; all package/profile data is owned.
internal static class MmcJavaDiscoveryChecks {
    internal static void Run(FixtureEnvironment environment) {
        using FileTaskScope files = new(environment);
        var profile = new JsonConfigProvider(Path.Combine(files.Root,"java-discovery-profile.json")); ConfigProviders.Default = profile;
        Native.Check(ReferenceEquals(Configs.JavaList.Provider,profile),"Java discovery captured an external profile.");
        string java8 = Tool("PCLLF_MMC_TEST_JAVA8"), java26 = Tool("PCLLF_MMC_TEST_JAVA26");
        var catalog8 = new Java(Path.GetDirectoryName(java8)!,new Version(26,0));
        var catalog26 = new Java(Path.GetDirectoryName(java26)!,new Version(8,0));
        Configs.JavaList.Set(new ConcurrentList<Java>([catalog26,catalog8])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
        Native.Cache("LoginType",ModLaunch.McLoginType.Legacy); Native.Cache("LaunchArgumentJavaSelect","");
        // The ordinary engine deliberately refreshes the public account type from Registry.
        // Substitute only that type predicate; every Registry/profile guard remains installed.
        object service=Native.Field(typeof(ModJava),"Service").GetValue(null)!;
        object javaHost=service.GetType().GetFields(Native.Flags).Single(field=>field.FieldType==typeof(LegacyJavaHost<ModMinecraft.McInstance>)).GetValue(service)!;
        object isNide=Native.Get(javaHost,"IsNideLogin")!;
        Native.Set(javaHost,"IsNideLogin",new Func<bool>(()=>false));
        int refreshes = 0; Action refreshed = () => Interlocked.Increment(ref refreshes); ModJava.JavaListRefreshWorker.Started += refreshed;
        try {
            using var old = new Fixture(Path.Combine(files.Root,"java8"),[8],8); files.SelectMinecraft(old.Game);
            Select(old.Owner,0);
            Native.Check(PageInstanceLeft.CurrentJavaWorker.LastResult?.Version?.Major==8,"MMC Java8 package did not select actual Java8.");
            Native.Check(Hint().Contains("Java 8",StringComparison.Ordinal),"Actual settings panel did not display selected Java8.");

            using var modern = new Fixture(Path.Combine(files.Root,"modern"),[17,21,25,26],26); files.SelectMinecraft(modern.Game);
            Select(modern.Owner,0);
            Native.Check(PageInstanceLeft.CurrentJavaWorker.LastResult?.Version?.Major==26 && Hint().Contains("26",StringComparison.Ordinal),"Modern package/published Java26 view regressed to MC1.7.10 Java8 or a fixed Java21.");
            Native.Check(catalog8.Version!.Major==26 && catalog26.Version!.Major==8,"Discovery mutated cached catalogue objects instead of clean-probing fresh objects.");
            string primary = Hash(File.ReadAllBytes(Path.Combine(modern.Version,"PCL","InstanceDefinition.json")));
            MmcLaunchPreferences.SetForcedJava(modern.Owner,new Java(Path.GetDirectoryName(java8)!));
            Select(modern.Owner,3);
            Native.Check(Preview(modern.Owner).State==InstanceJavaPreviewState.Rejected && Hint().Contains("强制 Java",StringComparison.Ordinal),"Manual incompatible Java was hidden or selected without compiler validation.");
            int starts = 0; Action started = () => Interlocked.Increment(ref starts); PageInstanceLeft.CurrentJavaWorker.Started += started;
            try { for(int index=0;index<3;index++) Controller().UpdateJavaUi(); Native.Check(starts==0 && !PageInstanceLeft.CurrentJavaWorker.Running,"A terminal managed rejection restarted endlessly."); }
            finally { PageInstanceLeft.CurrentJavaWorker.Started -= started; }

            Preferences(modern.Owner,new() { ["MmcJavaLocationMode"]="Custom",["VersionArgumentJavaV2"]="1",["VersionArgumentJavaRange"]="[26,27)" });
            Select(modern.Owner,1);
            Native.Check(PageInstanceLeft.CurrentJavaWorker.LastResult?.Version?.Major==26,"Custom full-version range did not use the actual launch selector.");
            Preferences(modern.Owner,new() { ["MmcJavaLocationMode"]="Custom",["VersionArgumentJavaV2"]="1",["VersionArgumentJavaRange"]=null! });
            Select(modern.Owner,1);
            Native.Check(!Settings.HasSaved("VersionArgumentJavaRange",modern.Owner) && Hint().Contains("17、21、25、26",StringComparison.Ordinal),"Entering MMC range mode silently stored a vanilla interval or lost discrete declarations.");
            Native.Check(!Hint().Contains("自动下载",StringComparison.Ordinal),"MMC settings promised an unsupported automatic Java download.");

            Preferences(modern.Owner,new() { ["MmcJavaLocationMode"]="Custom",["VersionArgumentJavaV2"]="2" });
            Select(modern.Owner,2);
            Native.Check(Preview(modern.Owner).State==InstanceJavaPreviewState.Rejected && Hint().Contains("实例目录",StringComparison.Ordinal),"Instance-folder-only mode used an outside catalogue Java.");
            Preferences(modern.Owner,new() { ["MmcJavaLocationMode"]="InheritGlobal",["VersionArgumentJavaV2"]="0" });
            Select(modern.Owner,0);
            using(var canceled = new CancellationTokenSource()) {
                canceled.Cancel(); Native.Static("PCL.MmcJavaDiscovery","Start",modern.Owner,PageInstanceLeft.CurrentJavaWorker,canceled.Token); Wait(); Controller().UpdateJavaUi();
                Native.Check(Preview(modern.Owner).State==InstanceJavaPreviewState.Canceled && Hint().Contains("已取消",StringComparison.Ordinal),"Pre-workload cancellation was replaced by an endless search or stale Java success.");
                Controller().UpdateJavaUi(); Native.Check(!PageInstanceLeft.CurrentJavaWorker.Running,"Canceled preview automatically restarted.");
            }
            Native.Check(Hash(File.ReadAllBytes(Path.Combine(modern.Version,"PCL","InstanceDefinition.json")))==primary,"Settings discovery adopted a variant or changed the published definition.");
            SwitchDuringRun(old,modern,false);
            SwitchDuringRun(old,modern,true);

            string sourcePath=modern.Definition.SourceBundle.MetadataRecords.First(record=>record.Kind==MmcSourceRecordKind.Manifest).ObjectRelativePath;
            File.AppendAllText(Path.Combine(modern.Version,sourcePath)," ");
            Select(modern.Owner,0);
            Native.Check(Preview(modern.Owner).State==InstanceJavaPreviewState.Failed && PageInstanceLeft.CurrentJavaWorker.LastResult is null,"Damaged retained sources fell back to ordinary Java8.");
            for(int index=0;index<3;index++) Controller().UpdateJavaUi();
            Native.Check(!PageInstanceLeft.CurrentJavaWorker.Running,"Source failure restarted indefinitely.");

            using var unknown = new Fixture(Path.Combine(files.Root,"unknown"),null,26); files.SelectMinecraft(unknown.Game); Select(unknown.Owner,0);
            Native.Check(PageInstanceLeft.CurrentJavaWorker.LastResult?.Version?.Major==26 && Hint().Contains("未声明",StringComparison.Ordinal),"Unspecified policy invented a Java8 constraint.");
            string pending=Path.Combine(unknown.Version,"PCL","MmcImportPending.json"); File.WriteAllText(pending,"owned pending marker");
            Controller().UpdateJavaUi(); Native.Check(Hint().Contains("未完成",StringComparison.Ordinal),"Pending MMC ownership did not show a diagnosis."); File.Delete(pending);
            Native.Cache("LoginType",ModLaunch.McLoginType.Nide);
            foreach(int mode in new[] { 0,2 }) {
                Preferences(unknown.Owner,new() { ["VersionServerLogin"]=mode.ToString() });
                var type=(ModLaunch.McLoginType)Native.Static("PCL.MmcJavaDiscovery","LoginType",unknown.Owner)!;
                var constraints=Native.Static("PCL.MmcLaunchAuthentication","JavaRequirementsForType",type)!;
                Native.Check(((ImmutableArray<MmcJavaConstraint>)Native.Property(constraints.GetType(),"Majors").GetValue(constraints)!).IsEmpty &&
                    Native.Property(constraints.GetType(),"MinimumVersion").GetValue(constraints) is null,"Viewing ordinary/offline B inherited selected A's global Nide floor.");
            }
            Native.Cache("LoginType",ModLaunch.McLoginType.Legacy);

            FullVersionConstraint(old,java8,java26);
            var ordinary=Ordinary(files,java8,java26);
            SwitchDuringRun(unknown,()=>ordinary,false,8);
            SwitchDuringRun(unknown,()=>ordinary,true,8);
            Native.Check(refreshes==0,"Java discovery started a global search/download worker.");
            InstanceConfigProviders.FlushAndDisposeWithin(files.Root); environment.AssertNoForbiddenIo();
            Console.WriteLine("PASS MMC actual settings/CurrentJavaWorker: Java8/26, stale catalogue, forced rejection, range/no default writes, folder, unknown policy, bad source, pending, cancellation and full-version constraints; no metadata network, download, login, game or variant adoption.");
        } finally {
            PageInstanceLeft.CurrentJavaWorker.Cancel(); PageInstanceLeft.CurrentJavaWorker.WaitIfRunning(30000);
            ModJava.JavaListRefreshWorker.Started -= refreshed;
            Native.Set(javaHost,"IsNideLogin",isNide);
        }
    }

    private static PageInstanceSetup Page = null!;
    private static SetupController Controller() => (SetupController)Native.Property(typeof(PageInstanceSetup),"Controller").GetValue(Page)!;
    private static string Hint() => ((MyHint)Native.Property(typeof(PageInstanceSetup),"HintArgumentJava").GetValue(Page)!).Text;
    private static InstanceJavaPreview Preview(ModMinecraft.McInstance owner) {
        var host=(InstanceSettingsHost<ModMinecraft.McInstance>)Native.Call(typeof(PageInstanceSetup),"CreateHost",Page)!;
        return host.ManagedJavaPreview!(owner,PageInstanceLeft.CurrentJavaWorker)!;
    }
    private static void Select(ModMinecraft.McInstance owner,int kind) {
        Console.WriteLine("INFO Java settings target "+owner.Name+", mode "+kind);
        PageInstanceLeft.CurrentJavaWorker.Cancel(); Wait(); PageInstanceLeft.Instance=owner;
        Page=new PageInstanceSetup(); ModMain.FrmInstanceSetup=Page;
        ((ComboBox)Native.Property(typeof(PageInstanceSetup),"ComboArgumentJava").GetValue(Page)!).SelectedIndex=kind;
        PageInstanceLeft.ReloadCurrentJava(); Wait(); Controller().UpdateJavaUi();
    }
    private static void Wait() {
        DateTime deadline=DateTime.UtcNow.AddSeconds(45);
        while(PageInstanceLeft.CurrentJavaWorker.Running) {
            Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline);
            if(DateTime.UtcNow>=deadline) throw new TimeoutException("Controlled actual Java preview did not finish."); Thread.Sleep(5);
        }
        Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline);
    }
    private static void Preferences(ModMinecraft.McInstance instance,Dictionary<string,string?> values) => MmcIniConfiguration.QueuePreferenceBatch(instance.Config,values);
    private static string Tool(string name) {
        string path=Environment.GetEnvironmentVariable(name)??throw new IOException("Set explicit installed Java: "+name);
        Native.Check(File.Exists(path),"Configured installed Java is absent: "+name); return path;
    }
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static void FullVersionConstraint(Fixture fixture,string java8,string java26) {
        var saved=MmcSavedSourceStore.LoadAsync(fixture.Paths,fixture.Definition).GetAwaiter().GetResult();
        void Select(MmcLaunchJavaPreference preference) => MmcLaunchJava.SelectFromCandidatesAsync(fixture.Owner,fixture.Definition,saved.Resolved.Resolution.Profile!,
            preference,[new(Path.GetDirectoryName(java8)!),new(Path.GetDirectoryName(java26)!)],CancellationToken.None,
            minimumVersion:new Version(8,0,9999)).GetAwaiter().GetResult();
        Expect("MMC_JAVA_NO_COMPATIBLE",()=>Select(new(MmcLaunchPreferenceMode.InheritGlobal,null!,false)));
        Expect("MMC_JAVA_PREFERENCE_INCOMPATIBLE",()=>Select(new(MmcLaunchPreferenceMode.Custom,java8,true)));
        var requirements=Native.Static("PCL.MmcLaunchAuthentication","JavaRequirementsForType",ModLaunch.McLoginType.Nide)!;
        var minimum=(Version)Native.Property(requirements.GetType(),"MinimumVersion").GetValue(requirements)!;
        var majors=(ImmutableArray<MmcJavaConstraint>)Native.Property(requirements.GetType(),"Majors").GetValue(requirements)!;
        Native.Check(minimum==new Version(8,0,141),"Settings/launch authentication lost the exact Nide update floor.");
        var compatible=MmcLaunchJava.SelectFromCandidatesAsync(fixture.Owner,fixture.Definition,saved.Resolved.Resolution.Profile!,
            new(MmcLaunchPreferenceMode.Custom,java8,true),[],CancellationToken.None,majors,minimumVersion:minimum).GetAwaiter().GetResult();
        Native.Check(compatible.Java.Version>=minimum,"Real installed Java8 failed the shared exact account version check.");
    }
    private static void Expect(string code,Action action) {
        try { action(); } catch(MmcLaunchJavaException error) when(error.Code==code) { return; }
        throw new Exception("Expected actual Java selection rejection "+code);
    }
    private static void SwitchDuringRun(Fixture first,Fixture second,bool failSource) => SwitchDuringRun(first,()=>new ModMinecraft.McInstance(second.Version).Load(),failSource,26);
    private static void SwitchDuringRun(Fixture first,Func<ModMinecraft.McInstance> createTarget,bool failSource,int expectedMajor) {
        string source=Path.Combine(first.Version,first.Definition.SourceBundle.MetadataRecords.First(record=>record.Kind==MmcSourceRecordKind.Manifest).ObjectRelativePath);
        byte[] original=File.ReadAllBytes(source);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim(); using var cancel=new CancellationTokenSource();
        Action blocked=()=> { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); };
        var worker=PageInstanceLeft.CurrentJavaWorker; worker.Cancel(); Wait();
        try {
            if(failSource) File.AppendAllText(source," ");
            PageInstanceLeft.Instance=first.Owner;
            worker.Started += blocked;
            Native.Static("PCL.MmcJavaDiscovery","Start",first.Owner,worker,cancel.Token);
            Native.Check(entered.Wait(TimeSpan.FromSeconds(5)) && worker.Running,"The genuine shared worker did not enter its running window.");
            var target=createTarget();
            int animation=ModAnimation.AniControlEnabled; ModAnimation.AniControlEnabled=animation+1;
            try {
                PageInstanceLeft.Instance=target; Page=new PageInstanceSetup(); ModMain.FrmInstanceSetup=Page;
                ((ComboBox)Native.Property(typeof(PageInstanceSetup),"ComboArgumentJava").GetValue(Page)!).SelectedIndex=0;
                Controller().UpdateJavaUi();
            } finally { ModAnimation.AniControlEnabled=animation; }
            Native.Check(worker.Running,"Switching pages canceled/drained A before exercising the shared running worker.");
            if(!failSource) cancel.Cancel();
            worker.Started -= blocked; release.Set(); Wait();
            var firstState=Preview(first.Owner);
            Native.Check(failSource ? firstState.State==InstanceJavaPreviewState.Failed && firstState.Message.Contains("保留来源",StringComparison.Ordinal)
                : firstState.State==InstanceJavaPreviewState.Canceled,"A's terminal result was not bound to its requested instance.");
            Controller().UpdateJavaUi(); Wait(); Controller().UpdateJavaUi();
            var host=(InstanceSettingsHost<ModMinecraft.McInstance>)Native.Call(typeof(PageInstanceSetup),"CreateHost",Page)!;
            Native.Check(host.JavaPreviewIsCurrent!(target,worker) && worker.LastResult?.Version?.Major==expectedMajor &&
                (expectedMajor==8 || Preview(target).State==InstanceJavaPreviewState.Selected),
                "A's failure/cancellation poisoned B instead of allowing B's automatic recognition.");
        } finally { worker.Started -= blocked; release.Set(); worker.Cancel(); Wait(); File.WriteAllBytes(source,original); }
    }
    private static ModMinecraft.McInstance Ordinary(FileTaskScope files,string java8,string java26) {
        string root=Path.Combine(files.Root,"ordinary","versions","1.7.10"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"1.7.10.json"),"""{"id":"1.7.10","mainClass":"net.minecraft.launchwrapper.Launch","minecraftArguments":"--tweakClass cpw.mods.fml.common.launcher.FMLTweaker","libraries":[{"name":"net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"}],"releaseTime":"2014-05-14T18:29:23Z","time":"2014-05-14T18:29:23Z","type":"release"}""");
        File.WriteAllText(Path.Combine(root,"1.7.10.jar"),"inert owned ordinary jar");
        var owner=new ModMinecraft.McInstance(root).Load(); FileTaskScope.CacheInstance(owner);
        Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java26)!),new(Path.GetDirectoryName(java8)!)]));
        Select(owner,0);
        Native.Check(PageInstanceLeft.CurrentJavaWorker.LastResult?.Version?.Major==8,"Ordinary MC1.7.10 no longer uses the legacy Java8 rule.");
        return owner;
    }

    private sealed class Fixture : IDisposable {
        internal readonly string Game,Version;
        internal readonly ModMinecraft.McInstance Owner;
        internal readonly MmcInstanceDefinition Definition;
        internal readonly MmcInstancePaths Paths;
        private readonly HttpClient client=new(new NoNetwork());
        internal Fixture(string root,int[]? majors,int runtimeMajor) {
            Game=Path.Combine(root,"minecraft"); Version=Path.Combine(Game,"versions","managed"); Directory.CreateDirectory(root);
            byte[] index=Encoding.UTF8.GetBytes("{\"objects\":{}}"); var cache=new MmcArtifactCacheRoots(Path.Combine(root,"cache","libraries"),Path.Combine(root,"cache","assets"),Path.Combine(root,"cache","sources"));
            string cached=Path.Combine(cache.AssetsRoot,"indexes","fixture.json"); Directory.CreateDirectory(Path.GetDirectoryName(cached)!); File.WriteAllBytes(cached,index);
            string archive=Path.Combine(root,"owned.zip");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)) {
                Add("mmc-pack.json","""{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
                var patch=new JObject { ["uid"]="net.minecraft",["version"]="1.7.10",["mainClass"]="fixture.Main",
                    ["mainJar"]=new JObject { ["name"]="fixture:main:1",["MMC-hint"]="local",["MMC-filename"]="main.jar" },
                    ["assetIndex"]=new JObject { ["id"]="fixture",["url"]="https://fixture.invalid/index",["sha1"]=Convert.ToHexStringLower(SHA1.HashData(index)),["size"]=index.Length } };
                if(majors is not null) patch["compatibleJavaMajors"]=new JArray(majors);
                Add("patches/net.minecraft.json",patch.ToString()); Add("libraries/main.jar","inert owned main"); Add("minecraft/config/author.cfg","author data");
                void Add(string path,string body) { using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false)); writer.Write(body); }
            }
            var backend=new MmcInstanceBackend<MmcArtifactDownloadSpec>(new(Path.Combine(root,"metadata"),new Uri("https://fixture.invalid/meta/"),client),cache,spec=>spec,
                (_,_)=>throw new IOException("Java UI fixture must use fixed cached inputs, never downloads."));
            var result=backend.ImportAsync(new(archive,Version,"managed",new("windows","x86_64",runtimeMajor))).GetAwaiter().GetResult();
            Native.Check(result.Status==MmcInstanceBackendStatus.Succeeded,"Owned Java UI fixture import failed: "+result.Failure);
            Definition=result.Definition!; Paths=result.Paths!; Owner=new(Version); Owner.Load();
        }
        public void Dispose()=>client.Dispose();
    }
    private sealed class NoNetwork : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => throw new IOException("Java UI preview must not use metadata/account network.");
    }
}
