using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS;
using PCLCS.Network;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;
using WatcherHost = PCLCS.LegacyWatcherHost<PCL.ModWatcher.Watcher,PCL.ModMinecraft.McInstance,PCL.ModLoader.LoaderTask<System.Diagnostics.Process,int>,PCL.ModWatcher.Watcher.MinecraftState>;

namespace PCLLF.Native.Facade.Tests;

/// <summary>Explicit real-game acceptance. Never register in default CI or substitute a test game.</summary>
internal static class MmcGtnhLaunchAcceptance {
    private const string ParentFlag="--mmc-gtnh-launch-acceptance",ChildFlag="--mmc-gtnh-launch-child";
    private const string ReconcileFlag="--mmc-gtnh-reconcile-owned-run";
    private static FixtureEnvironment? retainedEnvironment;
    private static Scope? retainedScope;
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    internal static bool TryRun(string[] args,out int code) {
        code=0;
        if(!args.Contains(ParentFlag,StringComparer.Ordinal)&&!args.Contains(ChildFlag,StringComparer.Ordinal)&&!args.Contains(ReconcileFlag,StringComparer.Ordinal))return false;
        try {code=args.Contains(ReconcileFlag,StringComparer.Ordinal)?ReconcileOwnedRun(args):args.Contains(ChildFlag,StringComparer.Ordinal)?RunChild(args):RunParent(args);}
        catch(Exception error){Console.Error.WriteLine("FAIL explicit GTNH game acceptance: "+error);code=1;}
        return true;
    }

    private static int ReconcileOwnedRun(string[] args) {
        string original=Full(Required(args,"--case-root")),report=Full(Required(args,"--launch-root"));
        var imported=VerifyOwnership(original);
        using var owner=Read(Path.Combine(report,"owner.json"));var o=owner.RootElement;
        string nonce=o.GetProperty("Nonce").GetString()!;
        Native.Check(o.GetProperty("Kind").GetString()=="PCLLF.GtnhLaunchAcceptance"&&Same(o.GetProperty("Root").GetString()!,report)&&
            Same(o.GetProperty("OriginalCase").GetString()!,original)&&Guid.TryParseExact(nonce,"N",out _),"Previous retained launch owner differs.");
        Program.ValidateRoot(o.GetProperty("Fixture").GetString()!);
        using var process=Read(Path.Combine(report,"launch-1-process.json"));var p=process.RootElement;
        var receipt=p.GetProperty("Receipt").Deserialize<MmcSessionReceipt>()??throw new IOException("Prior session receipt is missing.");
        Native.Check(p.GetProperty("Nonce").GetString()==nonce&&p.GetProperty("SessionId").GetGuid()==receipt.SessionId&&
            receipt.InstanceId==imported.InstanceId&&Same(receipt.CanonicalRoot,imported.InstanceRoot)&&receipt.DefinitionGeneration==imported.Generation,
            "Prior launch evidence belongs to another instance or session.");
        var pins=new MmcJavaPinStore(Path.Combine(report,"java-pins"));
        using var admission=MmcAdmissionGate.Acquire(imported.InstanceRoot,imported.InstanceId,TimeSpan.Zero);
        var authority=OwnedMutableAuthority(o.GetProperty("Fixture").GetString()!);
        var result=MmcSessions.Reconcile(admission,receipt.SessionId,new MmcSessionProcessInspector(),pins.ReleaseVerified,authority);
        Write(report,"reconciled-after-host-exit.json",new{Nonce=nonce,receipt.SessionId,Result=result,Keep=true});
        Native.Check(result.Observation==MmcSessionObservation.ProvenExited,"Prior process exit cannot be proved; all receipts and pins remain protected: "+result.Diagnostic);
        Console.WriteLine("PASS owned prior session exit reconciled using actual process identities; no game was killed or restarted.");
        return 0;
    }
    private static MmcMutableRuntimeAuthority OwnedMutableAuthority(string fixture)=>(MmcMutableRuntimeAuthority)
        Native.Call(typeof(MmcMutableRuntimeAuthority),"ForOwnedFixture",null,fixture)!;

    private static int RunParent(string[] args) {
        string original=Full(Required(args,"--case-root")); var imported=VerifyOwnership(original);
        string java=Full(Option(args,"--java-executable")??imported.SelectedJava);
        if(!File.Exists(java)||Path.GetFileName(java)!="java.exe")throw new IOException("An explicit installed java.exe is required; none is installed or downloaded by this runner.");
        int maximum=Number(args,"--max-memory-mib",6144,1024,8192),minimum=Number(args,"--min-memory-mib",1024,16,maximum);
        string repository=MmcGtnhAcceptance.FindRepository();
        string report=Path.Combine(repository,"artifacts","mmc-gtnh-launch-acceptance",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
        string fixture=Path.Combine(Path.GetTempPath(),"PclNativeFacade-"+Guid.NewGuid().ToString("N")),nonce=Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(report);Directory.CreateDirectory(fixture);Program.ValidateRoot(fixture);
        Write(report,"owner.json",new{SchemaVersion=1,Kind="PCLLF.GtnhLaunchAcceptance",Root=report,OriginalCase=original,Fixture=fixture,Nonce=nonce,Java=java,MaximumMiB=maximum,MinimumMiB=minimum,Keep=true});
        string output=Path.Combine(fixture,"output");Native.Call(typeof(Program),"CopyOutput",null,AppContext.BaseDirectory,output);
        var start=new ProcessStartInfo(Path.Combine(output,Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location)+".exe")) {
            UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=fixture,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(string value in new[]{ChildFlag,"--case-root",original,"--launch-root",report,"--nonce",nonce,"--java-executable",java,"--max-memory-mib",maximum.ToString(CultureInfo.InvariantCulture),"--min-memory-mib",minimum.ToString(CultureInfo.InvariantCulture),"--isolated","--fixture",fixture})start.ArgumentList.Add(value);
        using var child=Process.Start(start)??throw new IOException("Retained game host did not start.");
        Write(report,"host-process.json",new{HostPid=child.Id,Nonce=nonce,MainSha256=Hash(Native.Main.Location),Fixture=fixture,Keep=true});
        Console.WriteLine("RETAINED GTNH GAME ROOT: "+report);
        Task stdout=Pump(child.StandardOutput,Path.Combine(report,"host.stdout.log"),false),stderr=Pump(child.StandardError,Path.Combine(report,"host.stderr.log"),true);
        // No timeout, auto-restart, finally-kill or directory cleanup. The real Java session
        // and its pin/monitor must outlive the acceptance caller until positive exit proof.
        while(!child.WaitForExit(250)) { }
        Task.WaitAll(stdout,stderr);
        Write(report,"host-exited.json",new{HostPid=child.Id,child.ExitCode,Nonce=nonce,Keep=true});
        return child.ExitCode;
    }

    private static int RunChild(string[] args) {
        string report=Full(Required(args,"--launch-root")),original=Full(Required(args,"--case-root")),fixture=Full(Required(args,"--fixture")),nonce=Required(args,"--nonce");
        Program.ValidateRoot(fixture);
        Native.Check(args.Contains("--isolated",StringComparer.Ordinal)&&Guid.TryParseExact(nonce,"N",out _)&&Same(AppContext.BaseDirectory,Path.Combine(fixture,"output")),"Game acceptance child/output identity differs.");
        using(var owner=Read(Path.Combine(report,"owner.json"))) {
            var o=owner.RootElement;
            Native.Check(o.GetProperty("Kind").GetString()=="PCLLF.GtnhLaunchAcceptance"&&Same(o.GetProperty("Root").GetString()!,report)&&Same(o.GetProperty("OriginalCase").GetString()!,original)&&Same(o.GetProperty("Fixture").GetString()!,fixture)&&o.GetProperty("Nonce").GetString()==nonce&&
                Same(o.GetProperty("Java").GetString()!,Required(args,"--java-executable"))&&o.GetProperty("MaximumMiB").GetInt32()==Number(args,"--max-memory-mib",6144,1024,8192)&&o.GetProperty("MinimumMiB").GetInt32()==Number(args,"--min-memory-mib",1024,16,o.GetProperty("MaximumMiB").GetInt32()),"Game acceptance ownership marker differs.");
        }
        var imported=VerifyOwnership(original);
        retainedEnvironment=new(fixture);ModpackUpdateCleanup.VerifyIsolatedChild();
        retainedScope=new(retainedEnvironment,report,fixture,nonce,imported,Full(Required(args,"--java-executable")),Number(args,"--max-memory-mib",6144,1024,8192),Number(args,"--min-memory-mib",1024,16,8192));
        return retainedScope.Run();
    }

    private sealed record Imported(string CaseRoot,string InstanceRoot,string Sample,Guid InstanceId,long Generation,string ImportMainSha256,string SelectedJava,MmcRuntimeEnvironment Environment);
    private static Imported VerifyOwnership(string caseRoot) {
        string id=Path.GetFileName(caseRoot),run=Directory.GetParent(caseRoot)!.FullName;
        if(id is not("java8" or "java21")||Path.GetFileName(Directory.GetParent(run)!.FullName)!="mmc-gtnh-acceptance")throw new IOException("Select one retained, completed official acceptance case.");
        _=MmcGtnhAcceptance.OwnedCacheCase(run,id,Directory.GetParent(run)!.FullName);
        using var receipt=Read(Path.Combine(caseRoot,"import-result.json"));using var location=Read(Path.Combine(caseRoot,"fixture-location.json"));using var ended=Read(Path.Combine(caseRoot,"child-ended.json"));
        var r=receipt.RootElement;var l=location.RootElement;var e=ended.RootElement;
        string sha=id=="java8"?"ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af":"6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16";
        Native.Check(r.GetProperty("Status").GetString()=="Succeeded"&&r.GetProperty("Sample").GetString()==id&&r.GetProperty("ArchiveSha256").GetString()==sha&&
            Same(r.GetProperty("CaseRoot").GetString()!,caseRoot)&&Same(l.GetProperty("CaseRoot").GetString()!,caseRoot)&&r.GetProperty("Nonce").GetString()==l.GetProperty("Nonce").GetString()&&
            e.GetProperty("Nonce").GetString()==r.GetProperty("Nonce").GetString()&&e.GetProperty("Pid").GetInt32()==r.GetProperty("Pid").GetInt32()&&e.GetProperty("Received").GetBoolean(),"Import/owner/fixture/child receipt chain is incomplete or differs.");
        string oldFixture=l.GetProperty("Root").GetString()!;Program.ValidateRoot(oldFixture);
        string importMain=r.GetProperty("MainSha256").GetString()!;
        Native.Check(Hash(Path.Combine(oldFixture,"output","LivingPCL.dll"))==importMain,"Original import Main evidence changed.");
        string instance=Full(r.GetProperty("InstanceRoot").GetString()!);
        Native.Check(Same(instance,Path.Combine(caseRoot,"game","versions","GTNH-2.9.0-"+id)),"Imported instance escaped its owned acceptance case.");
        var inspection=MmcInstanceInspector.Read(instance);
        Native.Check(inspection.IsConsistent&&inspection.Definition!.InstanceId==r.GetProperty("InstanceId").GetGuid()&&inspection.Definition.Generation==r.GetProperty("Generation").GetInt64(),"Published instance identity/generation differs from the successful import receipt.");
        var environment=r.GetProperty("Environment").Deserialize<MmcRuntimeEnvironment>()??throw new IOException("Import environment missing.");
        Native.Check(environment.JavaMajor==(id=="java8"?8:21),"Receipt environment differs from the selected acceptance case.");
        return new(caseRoot,instance,id,inspection.Definition!.InstanceId,inspection.Definition.Generation,importMain,r.GetProperty("SelectedJava").GetString()!,environment);
    }

    private sealed class Scope {
        private readonly FixtureEnvironment environment;
        private readonly string report,fixture,nonce,java;
        private readonly Imported imported;
        private readonly int maximum,minimum;
        private readonly object logGate=new();
        private readonly ConcurrentQueue<string> observations=new();
        private readonly HashSet<string> controls=new(StringComparer.Ordinal);
        private readonly StreamWriter lifecycle;
        private ModMinecraft.McInstance instance=null!;
        private ModLoader.LoaderCombo<object>? combo;
        private Process? game;
        private MmcRuntimeLaunchPlan? runtime;
        private Guid sessionId;
        private string definitionSha="";
        private long generation;
        private int launch;
        private bool windowReported,exitReported;
        private string? lastError;
        private DateTime lastStatus;
        private object? context;
        private StreamWriter? gameLog;
        internal Scope(FixtureEnvironment environment,string report,string fixture,string nonce,Imported imported,string java,int maximum,int minimum) {
            this.environment=environment;this.report=report;this.fixture=fixture;this.nonce=nonce;this.imported=imported;this.java=java;this.maximum=maximum;this.minimum=minimum;
            lifecycle=new(Path.Combine(report,"lifecycle.log"),false,new UTF8Encoding(false)){AutoFlush=true};
        }
        internal int Run() {
            try { PrepareReadOnly();ConfigureOwnedProfile();StartGame(); }
            catch(Exception error){Failure(error);if(game is null)return 1;}
            for(;;) {
                try {
                    PumpUi();DrainObservations();ObserveGame();environment.AssertNoForbiddenIo();
                    if(DateTime.UtcNow-lastStatus>TimeSpan.FromSeconds(2)) {
                        lastStatus=DateTime.UtcNow;Write(report,"status.json",new{Launch=launch,HostPid=Environment.ProcessId,GamePid=game?.Id,SessionId=sessionId,Nonce=nonce,
                            State=combo?.State.ToString(),Progress=combo?.Progress,GameExited=game?.HasExited,WindowObserved=windowReported,ActiveSession=MmcLaunch.HasActiveSessions,Keep=true});
                    }
                    string command=Path.Combine(report,"control.json");
                    if(File.Exists(command)) {
                        using var document=Read(command);var c=document.RootElement;
                        string commandId=c.GetProperty("CommandId").GetString()!,action=c.GetProperty("Action").GetString()!;
                        Native.Check(c.GetProperty("Nonce").GetString()==nonce&&Guid.TryParseExact(commandId,"N",out _),"Control owner/identity differs.");
                        if(!controls.Add(commandId))throw new IOException("Control identity was already handled.");
                        File.Move(command,Path.Combine(report,"control-"+commandId+".json"),false);
                        if(game is not null&&!game.HasExited||MmcLaunch.HasActiveSessions)throw new IOException("Game or verified-exit cleanup is still active; no repair, relaunch or host stop is authorized.");
                        if(action=="finish") {Write(report,"finished.json",new{Status="FinishedAfterVerifiedExit",Nonce=nonce,Pid=Environment.ProcessId,Keep=true});return 0;}
                        if(action is "repair" or "repair-and-relaunch")Repair();
                        if(action is "relaunch" or "repair-and-relaunch")StartGame();
                        else if(action!="repair")throw new IOException("Unknown control action.");
                    }
                } catch(Exception error){
                    Failure(error);
                    if(game is null&&combo?.State==ModBase.LoadState.Failed&&context is not null) {
                        Guid attempted=(Guid)Native.Get(context,"sessionId")!;
                        if(!Directory.Exists(Path.Combine(imported.InstanceRoot,"PCL","MmcSessions",attempted.ToString("N"))))return 1;
                    }
                }
                Thread.Sleep(100);
            }
        }
        private void PrepareReadOnly() {
            var inspection=MmcInstanceInspector.Read(imported.InstanceRoot);
            Native.Check(inspection.IsConsistent&&inspection.Definition!.InstanceId==imported.InstanceId&&inspection.Definition.Generation==imported.Generation,"Owned definition changed before readonly preparation.");
            var paths=inspection.Paths!;var saved=MmcSavedSourceStore.LoadAsync(paths,inspection.Definition!).GetAwaiter().GetResult();
            var compiled=MmcProfileCompiler.Compile(saved.Resolved.Resolution.Profile!,imported.Environment);
            Native.Check(!compiled.HasErrors&&compiled.Profile?.RuntimePlanFingerprint==inspection.Definition!.InstalledProjection!.RuntimePlanFingerprint,"Saved source recompile differs from the exact imported runtime.");
            var plan=compiled.Profile!;using var receipt=Read(Path.Combine(imported.CaseRoot,"import-result.json"));var r=receipt.RootElement;
            Native.Check(plan.MainClass==r.GetProperty("MainClass").GetString()&&plan.MainJar.Key==r.GetProperty("MainJarKey").GetString(),"Imported entry/mainJar differs from current saved-source compilation.");
            var locks=inspection.Definition!.ArtifactLocks.ToDictionary(a=>a.ArtifactKey,StringComparer.Ordinal);
            foreach(var row in r.GetProperty("Libraries").EnumerateArray()) {
                var locked=locks[row.GetProperty("Key").GetString()!];string file=paths.ResolveRelative(MmcPathRole.Instance,locked.TargetRelativePath);
                Native.Check(locked.AdoptedSha256==row.GetProperty("AdoptedSha256").GetString()&&locked.TargetRelativePath==row.GetProperty("TargetRelativePath").GetString()&&new FileInfo(file).Length==locked.SizeBytes&&Hash(file)==locked.AdoptedSha256,"Imported active classpath/native bytes changed.");
            }
            foreach(var output in inspection.Definition.DerivedOutputs.Where(d=>d.Kind==MmcDerivedOutputKind.NativeFile)) {
                string file=paths.ResolveRelative(MmcPathRole.Instance,output.TargetRelativePath);Native.Check(new FileInfo(file).Length==output.SizeBytes&&Hash(file)==output.Sha256,"Physical native file differs from its published derivation.");
            }
            definitionSha=Hash(Path.Combine(paths.InstanceRoot,"PCL","InstanceDefinition.json"));generation=inspection.Definition.Generation;
            Write(report,"readonly-verified.json",new{Status="VerifiedBeforeAnyPreferenceWriteOrLaunch",InstanceRoot=paths.InstanceRoot,imported.InstanceId,Generation=generation,DefinitionSha256=definitionSha,
                ImportMainSha256=imported.ImportMainSha256,LaunchMainSha256=Hash(Native.Main.Location),Environment=imported.Environment,plan.MainClass,MainJarKey=plan.MainJar.Key,
                Classpath=plan.Classpath.Select(a=>new{a.Key,Path=locks[a.Key].TargetRelativePath,locks[a.Key].AdoptedSha256}),NativeFiles=inspection.Definition.DerivedOutputs.Where(d=>d.Kind==MmcDerivedOutputKind.NativeFile),Keep=true});
            ConfigProviders.Default=new JsonConfigProvider(Path.Combine(report,"private-profile.json"));
            ModBase.RunInUi();_=FileTaskShell.Install();MmcGtnhPostPublishChecks.InstallEmptyLegacyAccountStore();
            foreach(string owner in new[]{"PCL.LocalGameData","PCL.WorldBackups","PCL.EnvironmentHistory"})Native.Property(Native.Type(owner),"TestRoot").SetValue(null,Path.Combine(report,owner.Split('.').Last()));
            Native.Check(ReferenceEquals(Configs.JavaList.Provider,ConfigProviders.Default),"Game acceptance captured an external Java profile.");
            Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java)!)]));Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            Native.Property(typeof(MmcLaunch),"JavaPins").SetValue(null,new Func<MmcJavaPinStore>(()=>new(Path.Combine(report,"java-pins"))));
            var mutableAuthority=OwnedMutableAuthority(fixture);
            Native.Property(typeof(MmcLaunch),"MutableRuntimeAuthorityFactory").SetValue(null,new Func<MmcMutableRuntimeAuthority>(()=>mutableAuthority));
            Native.Property(typeof(MmcLaunch),"ConfirmVariantAdoption").SetValue(null,new Func<ModMinecraft.McInstance,MmcLaunchJavaSelection,bool>((_,_)=>throw new IOException("Acceptance requires its already-published environment, without adopting another Java variant.")));
            var network=(LegacyNetworkHost<ModNet.NetFile,ModNet.LoaderDownload,ModNet.NetManagerClass,ModLoader.LoaderBase>)Native.Field(typeof(ModNet),"NativeHost").GetValue(null)!;
            network.SignCdn=_=>throw new IOException("Real-game acceptance cannot redownload launcher inputs; preserve the published instance for recovery.");
            network.TempFolder=()=>Path.Combine(report,"network-temp")+Path.DirectorySeparatorChar;network.RequestTempFolder=()=>Path.Combine(report,"network-work-"+Guid.NewGuid().ToString("N"))+Path.DirectorySeparatorChar;
            foreach(var pair in new Dictionary<string,object>{["LaunchAdvanceJvm"]="",["LaunchAdvanceGame"]="",["LaunchAdvanceRun"]="",["LaunchAdvanceRunWait"]=true,["LaunchAdvanceDisableJLW"]=false,["LaunchArgumentJavaSelect"]="",
                ["LaunchRamType"]=1,["LaunchRamCustom"]=21,["LaunchArgumentWindowType"]=3,["LaunchArgumentWindowWidth"]="1280",["LaunchArgumentWindowHeight"]="720",["LaunchPersonalOptionsSync"]=false,["LaunchPersonalOptionsKeys"]=false,
                ["LaunchRecentWorlds"]=false,["UiMusicStop"]=false,["UiMusicStart"]=false,["LaunchArgumentVisible"]=1})Native.Cache(pair.Key,pair.Value);
            Native.Field(typeof(ModMinecraft),"_McFolderSelected").SetValue(null,Path.Combine(imported.CaseRoot,"game")+Path.DirectorySeparatorChar);
            instance=new(imported.InstanceRoot);Native.Field(typeof(ModMinecraft),"_McInstanceSelected").SetValue(null,instance);
            InstallUiBoundary();environment.AssertNoForbiddenIo();
        }
        private void ConfigureOwnedProfile() {
            var saved=MmcSavedSourceStore.LoadAsync(MmcInstancePaths.Resolve(imported.InstanceRoot),MmcInstanceInspector.Read(imported.InstanceRoot).Definition!).GetAwaiter().GetResult();
            var view=MmcIniConfiguration.CapturePreferences(instance.Config);
            Write(report,"original-package-preferences.json",new{PackageConfiguration=saved.PackageConfiguration,Ini=view.Ini.Where(p=>p.Key.StartsWith("Mmc",StringComparison.Ordinal)||p.Key.StartsWith("VersionRam",StringComparison.Ordinal)||p.Key is "VersionArgumentJavaV2" or "VersionAdvanceJvm"),Keep=true});
            File.Copy(Path.Combine(imported.InstanceRoot,"PCL","Setup.ini"),Path.Combine(report,"original-Setup.ini"),false);
            int slider=Enumerable.Range(0,4097).Single(v=>Math.Abs(InstanceRamPolicy.Manual(v)*1024-maximum)<0.01);
            var prefs=MmcLaunchPreferences.Capture(instance,saved.PackageConfiguration,Guid.NewGuid());
            // This explicit acceptance preference replaces its prior optional initial heap only;
            // required component/bootstrap JVM tokens remain untouched in the bound Core plan.
            string optional=string.Join(" ",prefs.RuntimeSettings.UserJvmTokens.Where(value=>!value.StartsWith("-Xms",StringComparison.Ordinal)).Select(QuoteToken));
            if(optional.Length>0)optional+=" ";optional+="-Xms"+minimum.ToString(CultureInfo.InvariantCulture)+"m";
            MmcLaunchPreferences.SetForcedJava(instance,new Java(Path.GetDirectoryName(java)!));
            Settings.SetInstanceUserPreference("VersionRamType",1,instance);Settings.SetInstanceUserPreference("VersionRamCustom",slider,instance);
            Settings.SetInstanceUserPreference("VersionAdvanceJvm",optional,instance);
            Settings.Set("VersionPersonalOptionsSync",0,instance);Settings.Set("VersionWorldBackupAutoEnabled",false,instance);
            MmcLaunchPreferences.SetWindowOverride(instance,1280,720);instance.Config.Save();
            Write(report,"test-preferences.json",new{MaximumMiB=maximum,MinimumMiB=minimum,Slider=slider,CalculatedMiB=InstanceRamPolicy.Manual(slider)*1024,InstanceRoot=imported.InstanceRoot,OnlyOwnedAcceptanceInstance=true,Keep=true});
        }
        private void InstallUiBoundary() {
            var host=(WatcherHost)Native.Field(typeof(ModWatcher),"Host").GetValue(null)!;
            // Keep ALL HWND enumeration/class/title/visibility/PID APIs genuine for this acceptance.
            host.LaunchLog=(text,_)=>observations.Enqueue(text);host.RefreshShutdownButton=()=>{};host.RestoreLauncher=()=>{};host.EndProgram=()=>{};host.CrashHint=()=>{};host.FeedbackInfo=()=>{};
            var analyzer=host.CreateCrashAnalyzer;
            host.CreateCrashAnalyzer=selected=>new RetainedCrashOutput(analyzer(selected),report,nonce);
        }
        private sealed class RetainedCrashOutput(ILegacyCrashAnalyzer inner,string root,string nonce):ILegacyCrashAnalyzer {
            public void Collect(string game,IList<string>? log=null)=>inner.Collect(game,log);
            public bool Prepare()=>inner.Prepare();
            public void Analyze()=>inner.Analyze();
            public void Output(bool manual,List<string>? files=null)=>Write(root,"crash-analysis-without-dialog.json",new {
                Status="ProductCrashAnalysisCompleted_PresentationSuppressed",Nonce=nonce,Keep=true
            });
        }
        private void StartGame() {
            var current=MmcInstanceInspector.Read(imported.InstanceRoot);Native.Check(current.IsConsistent&&current.Definition!.InstanceId==imported.InstanceId&&current.Definition.Generation==generation&&Hash(Path.Combine(imported.InstanceRoot,"PCL","InstanceDefinition.json"))==definitionSha,"Authorized owned definition changed before this launch.");
            if(game is not null&&!game.HasExited||MmcLaunch.HasActiveSessions)throw new IOException("One real game at a time; previous Java or cleanup remains active.");
            launch++;windowReported=false;exitReported=false;runtime=null;game=null;sessionId=Guid.Empty;
            gameLog=new(Path.Combine(report,"game-"+launch+".redacted.log"),false,new UTF8Encoding(false)){AutoFlush=true};
            ModLaunch.McLoginLoader=new("Owned empty offline account boundary",_=>throw new IOException("Acceptance may not perform account IO.")) {
                Output=new(){Name="PCLLF_GTNH_TEST",Uuid=imported.InstanceId.ToString("N"),AccessToken="owned-offline-"+Guid.NewGuid().ToString("N"),ClientToken="owned-offline-client",Type="Legacy",ProfileJson="{}"}
            };
            var options=new ModLaunch.McLaunchOptions{Instance=instance};ModLaunch.CurrentLaunchOptions=options;
            var steps=MmcLaunch.CreateSteps(instance,options,selected=>ModLaunch.McLaunchJavaSelected=selected);
            context=steps.OfType<LegacyLaunchProcessStep>().Single().Work.Target;
            Native.Property(typeof(MmcLaunch),"ProcessCreated").SetValue(null,new Action<Guid,Process>(ObserveStarted));
            var operation=(LegacyLaunchOperation)Native.Static("PCL.ModLaunch","CreateLaunchCombo","Real GTNH "+imported.Sample+" launch "+launch,steps)!;
            combo=(ModLoader.LoaderCombo<object>)operation.Source;ModLaunch.McLaunchLoaderReal=combo;ModLoader.LoaderTaskbar.Add(combo);operation.Start();
            Write(report,"launch-"+launch+"-requested.json",new{Status="PreparingActualMainLaunch",Launch=launch,HostPid=Environment.ProcessId,Nonce=nonce,DefinitionSha256=definitionSha,Generation=generation,Keep=true});
        }
        private void ObserveStarted(Guid id,Process child) {
            game=child;sessionId=id;
            try {
                runtime=(MmcRuntimeLaunchPlan)Native.Get(context!,"runtime")!;
                child.OutputDataReceived+=(_,e)=>GameLine("stdout",e.Data);child.ErrorDataReceived+=(_,e)=>GameLine("stderr",e.Data);
                var sessions=(ConcurrentDictionary<Guid,MmcRunningSession>)Native.Field(typeof(MmcLaunch),"Active").GetValue(null)!;var running=sessions[id];
                Write(report,"launch-"+launch+"-process.json",new{Status="ActualJavaStarted",Launch=launch,HostPid=Environment.ProcessId,GamePid=child.Id,SessionId=id,Nonce=nonce,
                    Receipt=running.Receipt,Registration=running.Registration,MainSha256=Hash(Native.Main.Location),Java=runtime.Request.Java,
                    CommandUtf16LengthIncludingNul=MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(child.StartInfo.FileName,child.StartInfo.ArgumentList),ArgumentCount=child.StartInfo.ArgumentList.Count,
                    runtime.NonSensitiveArgumentFingerprint,runtime.RuntimeInputFingerprint,RedactedArguments=runtime.RedactedArguments,Classpath=runtime.OrderedGameClasspath,
                    runtime.Inputs,runtime.MutableDirectories,NativeDirectory=runtime.Request.Directories.NativeDirectory,AssetsRoot=runtime.Request.Directories.AssetsRoot,Keep=true});
                Console.WriteLine("REAL GTNH JAVA STARTED pid="+child.Id+" session="+id.ToString("N")+" report="+report);
            } catch(Exception error){Failure(error);}
        }
        private void GameLine(string stream,string? text) {
            if(text is null)return;
            string safe=runtime?.Redact(text)??"[runtime redactor unavailable]";
            lock(logGate)gameLog?.WriteLine(DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" "+stream+" "+safe);
        }
        private void DrainObservations() {while(observations.TryDequeue(out string? text)){string safe=runtime?.Redact(text)??text;lock(logGate)lifecycle.WriteLine(DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" "+safe);}}
        private void ObserveGame() {
            CapturePreparedPlan();
            if(game is null) {if(combo?.State==ModBase.LoadState.Failed)throw new IOException("Actual Main preparation failed.",combo.Error);return;}
            if(!game.HasExited) {
                var window=Windows(game.Id).FirstOrDefault();
                if(window is not null) {
                    Write(report,"window-observed.json",new{Status="RealNativeGameWindowObserved_NotAMenuAssertion",Launch=launch,GamePid=game.Id,SessionId=sessionId,Nonce=nonce,window.Handle,window.Title,window.ClassName,window.Visible,window.Left,window.Top,window.Right,window.Bottom,WatcherState=ModLaunch.McLaunchWatcher?.State.ToString(),Keep=true});
                    if(!windowReported){windowReported=true;Console.WriteLine("REAL GTNH WINDOW pid="+game.Id+" hwnd="+window.Handle+" title="+window.Title+"; CUA must verify menu/world.");}
                }
                return;
            }
            if(exitReported||MmcLaunch.HasActiveSessions)return;
            game.WaitForExit();DrainObservations();gameLog?.Flush();
            string history=Path.Combine(imported.InstanceRoot,"PCL","MmcSessionHistory",sessionId.ToString("N"));
            Write(report,"launch-"+launch+"-exit.json",new{Status="ActualJavaAndMonitoringExited",GamePid=game.Id,game.ExitCode,SessionId=sessionId,Nonce=nonce,WatcherState=ModLaunch.McLaunchWatcher?.State.ToString(),
                ExitedRecord=File.Exists(Path.Combine(history,"Exited.record")),LiveMutableGone=runtime is null?false:!Directory.Exists(runtime.MutableDirectories.SessionRoot),
                PinsReleased=!Directory.Exists(Path.Combine(report,"java-pins","pins"))||!Directory.EnumerateFiles(Path.Combine(report,"java-pins","pins"),"*.json",SearchOption.AllDirectories).Any(),
                CurrentDefinitionSha256=Hash(Path.Combine(imported.InstanceRoot,"PCL","InstanceDefinition.json")),Keep=true});
            exitReported=true;Console.WriteLine("REAL GTNH EXIT pid="+game.Id+" code="+game.ExitCode+"; host retained for explicit control.");
        }
        private void Repair() {
            var before=MmcInstanceInspector.Read(imported.InstanceRoot);Native.Check(before.IsConsistent&&before.Definition!.InstanceId==imported.InstanceId&&before.Definition.Generation==generation,"Repair input identity changed.");
            using var metadataClient=new System.Net.Http.HttpClient(new NoNetwork());
            string cache=Path.Combine(imported.CaseRoot,"game","PCL","MmcCache");
            var backend=new MmcInstanceBackend<MmcArtifactDownloadSpec>(new MmcMetadataStore(Path.Combine(cache,"metadata"),new Uri("https://acceptance.invalid/"),metadataClient),
                new(Path.Combine(imported.CaseRoot,"game","libraries"),Path.Combine(imported.CaseRoot,"game","assets"),Path.Combine(cache,"sources")),spec=>spec,
                (_,_)=>throw new IOException("Acceptance repair has no authority to redownload missing launcher inputs."));
            var result=backend.RepairAsync(new(imported.InstanceRoot,imported.Environment,imported.InstanceId){ExpectedGeneration=generation,ExpectedPrimarySha256=definitionSha}).GetAwaiter().GetResult();
            Native.Check(result.Status==MmcInstanceBackendStatus.Succeeded,"Actual dedicated repair did not succeed: "+result.Failure);
            generation=result.Definition!.Generation;definitionSha=Hash(Path.Combine(imported.InstanceRoot,"PCL","InstanceDefinition.json"));instance.Load();
            Write(report,"repair-"+generation+".json",new{Status="ActualRepairSucceededAfterVerifiedExit",imported.InstanceId,Generation=generation,DefinitionSha256=definitionSha,Nonce=nonce,Keep=true});
        }
        private void Failure(Exception error) {
            CapturePreparedPlan();
            string safe=runtime?.Redact(error.ToString())??error.ToString();
            if(safe==lastError)return;lastError=safe;
            Write(report,"failure.json",new{Status="Failed_NoAutomaticRestartOrKill",Launch=launch,HostPid=Environment.ProcessId,GamePid=game?.Id,SessionId=sessionId,Nonce=nonce,Error=safe,Keep=true});
            Console.Error.WriteLine(safe);
        }
        private void CapturePreparedPlan() {
            if(runtime is not null||context is null||Native.Get(context,"runtime") is not MmcRuntimeLaunchPlan built)return;
            runtime=built;var start=built.CreateStartInfo();
            Write(report,"launch-"+launch+"-prepared-plan.json",new{Status="ActualBuilderPlanObservedBeforeProcess",Launch=launch,MainSha256=Hash(Native.Main.Location),Java=built.Request.Java,
                built.Request.BoundPlan.DefinitionGeneration,built.Request.BoundPlan.BoundFingerprint,built.NonSensitiveArgumentFingerprint,built.RuntimeInputFingerprint,
                CommandUtf16LengthIncludingNul=MmcRuntimeWindowsCommandLine.LengthIncludingTerminator(start.FileName,start.ArgumentList),ArgumentCount=start.ArgumentList.Count,
                RedactedArguments=built.RedactedArguments,LogicalJvmArguments=built.LogicalJvmArguments,Classpath=built.OrderedGameClasspath,built.Inputs,built.MutableDirectories,
                NativeDirectory=built.Request.Directories.NativeDirectory,AssetsRoot=built.Request.Directories.AssetsRoot,Keep=true});
        }
    }

    private sealed class NoNetwork:System.Net.Http.HttpMessageHandler {protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)=>throw new IOException("No launcher network is permitted during retained game acceptance.");}
    private sealed record Window(long Handle,string Title,string ClassName,bool Visible,int Left,int Top,int Right,int Bottom);
    private static List<Window> Windows(int pid) {
        List<Window> result=[];EnumWindows((handle,_)=>{GetWindowThreadProcessId(handle,out uint owner);if(owner!=(uint)pid)return true;
            StringBuilder title=new(1024),type=new(256);GetWindowText(handle,title,title.Capacity);GetClassName(handle,type,type.Capacity);GetWindowRect(handle,out var rect);
            if(title.Length>0&&type.ToString() is "LWJGL" or "GLFW30" or "SunAwtFrame" or "SDL_app")result.Add(new(handle.ToInt64(),title.ToString(),type.ToString(),IsWindowVisible(handle),rect.Left,rect.Top,rect.Right,rect.Bottom));return true;},IntPtr.Zero);return result;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect {public int Left,Top,Right,Bottom;}
    private delegate bool EnumCallback(IntPtr handle,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumCallback callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr handle,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    private static void PumpUi(){var dispatcher=Dispatcher.CurrentDispatcher;var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Send,dispatcher){Interval=TimeSpan.FromMilliseconds(100)};timer.Tick+=(_,_)=>frame.Continue=false;timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}}
    private static string QuoteToken(string value)=>"\""+value.Replace("\\","\\\\",StringComparison.Ordinal).Replace("\"","\\\"",StringComparison.Ordinal)+"\"";
    private static void Write<T>(string root,string name,T value){string path=Path.Combine(root,name),temporary=path+".pending";using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,JsonOptions);stream.Flush(true);}File.Move(temporary,path,true);}
    private static void NoLinks(string path){for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Acceptance record or ancestor became a link.");}
    private static JsonDocument Read(string path){NoLinks(path);using var stream=File.OpenRead(path);if(stream.Length>16*1024*1024)throw new IOException("Acceptance record exceeds its budget.");return JsonDocument.Parse(stream);}
    private static string Hash(string path){NoLinks(path);using var stream=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(stream));}
    private static string Full(string path)=>Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    private static bool Same(string a,string b)=>string.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase);
    private static string Required(string[] args,string key)=>Option(args,key)??throw new ArgumentException("Missing "+key);
    private static string? Option(string[] args,string key){int index=Array.IndexOf(args,key);return index<0?null:index+1<args.Length?args[index+1]:throw new ArgumentException("Missing "+key);}
    private static int Number(string[] args,string key,int fallback,int minimum,int maximum){string? value=Option(args,key);int result=value is null?fallback:int.Parse(value,CultureInfo.InvariantCulture);if(result<minimum||result>maximum)throw new ArgumentOutOfRangeException(key);return result;}
    private static async Task Pump(StreamReader input,string path,bool error){await using var output=new StreamWriter(path,false,new UTF8Encoding(false)){AutoFlush=true};string? line;while((line=await input.ReadLineAsync())is not null){await output.WriteLineAsync(line);(error?Console.Error:Console.Out).WriteLine(line);}}
}
