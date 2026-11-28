using System.Collections.Concurrent;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using MeloongCore;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLCS.Network;
using PCLCS.Loaders;
using InstallHost = PCLCS.MinecraftInstallHost<PCL.ModLoader.LoaderBase, PCL.ModNet.NetFile, PCL.ModMinecraft.McInstance, PCL.ModDownload.DlForgeVersionEntry, PCL.ModDownload.DlNeoForgeListEntry, PCL.ResourceVersion, PCL.ModMinecraft.McLibEntry>;

namespace PCLLF.Native.Facade.Tests;

// Every task/combination, InstallGame request and cleanup callback remains real.
// Retained until the dedicated owned process exits: manager loops never observe
// restored production roots, settings or transient delegates.
internal sealed class PackRootInstallChecksScope
{
    internal sealed class InstallCase(string name, string folder, string zip, string metadataUrl, bool hold, bool fail, bool pauseAdmission) {
        internal readonly string Name=name, Folder=folder, Zip=zip, MetadataUrl=metadataUrl;
        internal readonly bool Hold=hold, Fail=fail;
        internal readonly AdmissionPause? Admission=pauseAdmission?new():null;
        internal readonly ManualResetEventSlim MetadataEntered=new(), DownloadEntered=new(), Release=new();
        internal readonly Exception Failure=new InvalidOperationException("owned synthetic metadata refusal");
    }
    internal sealed class AdmissionPause {
        internal readonly ManualResetEventSlim Scheduled=new(), Release=new(), Returned=new();
        internal Thread? Producer;
        internal int Claimed;
    }
    private readonly ConcurrentDictionary<ModLoader.LoaderBase,AdmissionPause> admissionPauses=new();
    private readonly FixtureEnvironment environment;
    private readonly FileTaskScope files;
    private readonly object packHost;
    private readonly InstallHost installHost;
    private readonly PackRootInstallChecksLoopback server;
    private readonly ConcurrentBag<Thread> taskThreads=new(), networkThreads=new(), cleanupThreads=new();
    private readonly ConcurrentDictionary<ModLoader.LoaderBase,byte> observedTasks=new();
    private readonly ConcurrentDictionary<Thread,byte> transferThreads=new();
    private string? firstMetadataPath;
    private readonly List<ModLoader.LoaderCombo<string>> roots=new();
    private readonly List<string> completeFiles=new();
    private readonly Dictionary<string,string> redirects=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> unknownSources=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> cleanupEvents=new();
    private InstallCase? current;
    private int nextTemp, cleanupRunning, networkForbidden;
    internal readonly string Root, Game, LibraryFile, AssetIndexFile, AssetObjectFile;
    internal readonly byte[] JarBytes, LibraryBytes, AssetBytes, AssetIndexBytes;
    internal readonly List<(string Text,bool Red)> Hints=new();
    internal readonly List<ModpackMessage> Messages=new();
    internal int InstallGameCalls, Starts, LibraryAnalyses, AssetAnalyses, NamePrompts;
    internal int Forbidden => Volatile.Read(ref networkForbidden);
    internal IReadOnlyList<Thread> NetworkThreads => networkThreads.ToArray();
    internal int StoppedTransfers => transferThreads.Count;
    internal bool TransportUsed => firstMetadataPath is not null && new[]{firstMetadataPath,"/client.jar","/library.jar","/asset-index.json","/asset-object"}.All(server.Requested);
    internal PackRootInstallChecksScope(FixtureEnvironment environment)
    {
        this.environment=environment; ModpackUpdateCleanup.VerifyIsolatedChild();
        Logger.Instance=new CleanupObserver(Logger.Instance,cleanupEvents);
        files=new(environment); Root=Path.Combine(files.Root,"pack-root-install");
        Native.Check(!Directory.Exists(Root),"Refusing existing HMCL root fixture."); Directory.CreateDirectory(Root);
        Game=Path.Combine(Root,"minecraft")+Path.DirectorySeparatorChar; Directory.CreateDirectory(Game); files.SelectMinecraft(Game.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        Native.Check(ModMinecraft.McFolderSelected==Game,"HMCL fixture must bind the exact selected Minecraft root with one trailing separator."); _=FileTaskShell.Install();
        // Pause only a selected real root's original Update action at the
        // existing scheduling boundary. Keep the original FileTaskScope observer
        // and scheduler, every platform guard and all actual loader work intact.
        var scheduler=(LegacyLoaderHost<ModLoader.LoaderBase,ModBase.LoadState,LauncherLoadingState>)Native.Field(typeof(ModLoader),"NativeLoaderHost").GetValue(null)!;
        Action<Action> run=scheduler.RunInThread;
        scheduler.RunInThread=work=>{
            Type? target=work.Target?.GetType();
            ModLoader.LoaderBase? owner=target?.IsGenericType==true&&target.GetGenericTypeDefinition()==typeof(LegacyLoaderComboEngine<,,,,>)&&work.Method.Name=="Update"
                ? (ModLoader.LoaderBase?)Native.Get(work.Target!,"owner") : null;
            if(owner is not null&&admissionPauses.TryGetValue(owner,out AdmissionPause? pause)&&Interlocked.CompareExchange(ref pause.Claimed,1,0)==0)
                run(()=>{Volatile.Write(ref pause.Producer,Thread.CurrentThread);pause.Scheduled.Set();try{if(!pause.Release.Wait(10000))throw new TimeoutException("Owned real root admission phase was not released.");work();}finally{pause.Returned.Set();}});
            else run(work);
        };
        JarBytes=Jar("client"); LibraryBytes=Jar("library"); AssetBytes=Encoding.UTF8.GetBytes("actual synthetic asset object with immutable hash");
        string hash=Sha(AssetBytes), index="pcllf-root-"+Guid.NewGuid().ToString("N");
        AssetIndexBytes=Encoding.UTF8.GetBytes(new JObject { ["objects"]=new JObject { ["fixture/root.txt"]=new JObject { ["hash"]=hash,["size"]=AssetBytes.Length } } }.ToString());
        LibraryFile=Path.Combine(Game,"libraries","fixture","root","1.0","root-1.0.jar"); AssetIndexFile=Path.Combine(Game,"assets","indexes",index+".json"); AssetObjectFile=Path.Combine(Game,"assets","objects",hash[..2],hash);
        server=new(); server.Add("/client.jar",JarBytes);server.Add("/library.jar",LibraryBytes);server.Add("/asset-index.json",AssetIndexBytes);server.Add("/asset-object",AssetBytes);
        foreach(string prefix in new[]{"https://resources.download.minecraft.net/","https://bmclapi2.bangbang93.com/assets/"}) redirects.Add(prefix+hash[..2]+"/"+hash,server.Url("/asset-object"));
        foreach(string prefix in new[]{"https://libraries.minecraft.net/","https://bmclapi2.bangbang93.com/maven/","https://bmclapi2.bangbang93.com/libraries/"}) redirects.Add(prefix+"fixture/root/1.0/root-1.0.jar",server.Url("/library.jar"));
        Metadata=new JObject { ["id"]="1.20.1", ["type"]="release", ["mainClass"]="fixture.NotExecutableMain", ["releaseTime"]="2023-06-12T13:25:51+00:00", ["time"]="2023-06-12T13:25:51+00:00", ["arguments"]=new JObject { ["game"]=new JArray(),["jvm"]=new JArray() }, ["minimumLauncherVersion"]=21,
            ["downloads"]=new JObject { ["client"]=new JObject { ["url"]=server.Url("/client.jar"),["sha1"]=Sha(JarBytes),["size"]=JarBytes.Length } }, ["assets"]=index,
            ["assetIndex"]=new JObject { ["id"]=index,["url"]=server.Url("/asset-index.json"),["sha1"]=Sha(AssetIndexBytes),["size"]=AssetIndexBytes.Length },
            ["libraries"]=new JArray { new JObject { ["name"]="fixture:root:1.0",["downloads"]=new JObject { ["artifact"]=new JObject { ["path"]="fixture/root/1.0/root-1.0.jar",["url"]=server.Url("/library.jar"),["sha1"]=Sha(LibraryBytes),["size"]=LibraryBytes.Length } } } } };
        // The existing eagerly-created HttpClient stays real. Its native URL
        // signing boundary admits only loopback or these exact synthetic paths.
        var network=(LegacyNetworkHost<ModNet.NetFile,ModNet.LoaderDownload,ModNet.NetManagerClass,ModLoader.LoaderBase>)Native.Field(typeof(ModNet),"NativeHost").GetValue(null)!;
        Native.Check(ModNet.NetManager.FileRemain==0 && ModNet.NetManager.Tasks.IsEmpty,"HMCL requires a fresh real downloader.");
        network.SignCdn=source => { if(source=="")return source; if(server.Owns(source))return source; if(redirects.TryGetValue(source,out string? target))return target; Interlocked.Increment(ref networkForbidden);unknownSources.TryAdd(source,0);throw new InvalidOperationException("Unselected HMCL transport source; no external request is permitted."); };
        var sign=network.SignHeaders;network.SignHeaders=(string url,ref System.Net.Http.HttpRequestMessage request,bool browser,bool credentials) => { Native.Check(server.Owns(url),"HMCL HTTP escaped its exact owned loopback endpoint.");sign(url,ref request,browser,credentials);Native.Check(!request.Headers.Contains("Authorization")&&!request.Headers.Contains("x-api-key"),"HMCL loopback must not carry account credentials."); };
        string networkTemp=Temp("network"), gameTemp=Temp("game-temp"), pure=Temp("game-pure");
        network.TempFolder=()=>networkTemp; network.RequestTempFolder=()=>Temp("network-work");network.AppData=()=>Path.Combine(Root,"owned-appdata")+Path.DirectorySeparatorChar;network.MinecraftFolders=()=>[Game];
        var runNetwork=network.RunNewThread;network.RunNewThread=(work,name,priority)=>{Thread thread=runNetwork(work,name,priority);networkThreads.Add(thread);return thread;};
        object installEngine=Native.Field(typeof(ModDownloadLib),"Service").GetValue(null)!;installHost=(InstallHost)Native.Get(installEngine,"host")!;
        installHost.RequestTempFolder=_=>Temp("game");installHost.TempFolder=()=>gameTemp;installHost.PureFolder=()=>pure;
        installHost.ClientJsonUrl=id=>{InstallCase value=current??throw new InvalidOperationException("Unexpected metadata lookup before root admission.");Native.Check(id=="1.20.1","HMCL changed requested vanilla version.");value.MetadataEntered.Set();if(value.Hold&&value.Fail&&!value.Release.Wait(10000))throw new TimeoutException("Owned metadata phase was not released.");if(value.Fail)throw value.Failure;return value.MetadataUrl;};
        var libraries=installHost.LibraryFiles;installHost.LibraryFiles=instance=>{Interlocked.Increment(ref LibraryAnalyses);return libraries(instance);};
        var assets=installHost.AssetFiles;installHost.AssetFiles=(instance,hashCheck,loader)=>{Interlocked.Increment(ref AssetAnalyses);return assets(instance,hashCheck,loader);};
        var background=installHost.RunInThread;installHost.RunInThread=work=>{Interlocked.Increment(ref cleanupRunning);background(()=>{cleanupThreads.Add(Thread.CurrentThread);cleanupEvents.Enqueue("cleanup-enter "+DateTime.UtcNow.ToString("O"));try{work();}catch(Exception error){cleanupEvents.Enqueue(error.ToString());throw;}finally{cleanupEvents.Enqueue("cleanup-return "+DateTime.UtcNow.ToString("O"));Interlocked.Decrement(ref cleanupRunning);}});};
        installHost.RefreshDownload=()=>{};installHost.RibbleDownload=()=>{};installHost.RefreshInstanceList=()=>{};
        installHost.Hint=(text,kind)=>Hints.Add((text,kind==1));installHost.Message=message=>{Messages.Add(message);return 1;};
        foreach(string field in new[]{"RetryRequest","LoaderRequest","NeoForge","ForgeList","SelectJava","ExtractResources","RunInNewThread","SaveFile","SelectFolder","OpenWebsite","BeforeMinecraftChange","CaptureEnvironment"})Guard(installHost,field);
        object packEngine=Native.Field(typeof(ModModpack),"Service").GetValue(null)!;packHost=Native.Get(packEngine,"host")!;
        Set(packHost,"RequestTempFolder",new Func<string>(()=>Temp("pack")));
        var names=(Func<string,ModpackNameValidator>)Native.Get(packHost,"NameValidator")!;
        Set(packHost,"NameValidator",new Func<string,ModpackNameValidator>(parent=>{var real=names(parent);return new(){Validate=real.Validate,Prompt=()=>{NamePrompts++;return null;}};}));
        var install=(Func<string,string,McComponents,List<ModLoader.LoaderBase>>)Native.Get(packHost,"InstallGame")!;
        Set(packHost,"InstallGame",new Func<string,string,McComponents,List<ModLoader.LoaderBase>>((name,folder,components)=>{InstallGameCalls++;Native.Check(components.MinecraftVersion=="1.20.1"&&components.ModLoaderVersions.Count==0,"HMCL requested unexpected game components.");return install(name,folder,components);}));
        var start=(Action<ModLoader.LoaderBase,string>)Native.Get(packHost,"Start")!;
        Set(packHost,"Start",new Action<ModLoader.LoaderBase,string>((loader,folder)=>{var root=(ModLoader.LoaderCombo<string>)loader;Native.Check(root.GetType().Assembly==Native.Main,"Root combo must be genuine Main.");roots.Add(root);Starts++;foreach(var node in AllNodes(root))Observe(node);if(current?.Admission is AdmissionPause pause)Native.Check(admissionPauses.TryAdd(root,pause),"A root admission gate was reused.");start(loader,folder);}));
        Set(packHost,"RefreshDownload",new Action(()=>{}));Set(packHost,"RibbleDownload",new Action(()=>{}));Set(packHost,"Hint",new Action<string,bool>((text,red)=>Hints.Add((text,red))));Set(packHost,"Message",new Func<ModpackMessage,int>(message=>{Messages.Add(message);return 1;}));
        foreach(string field in new[]{"ModRequest","CurseForgeProjects","Download","OpenExplorer","StartProcess","EndProgram","SelectPackFile","SelectInstallFolder","WaitOptiFine","OptiFineEntries","RibbleDownload"})Guard(packHost,field);
    }
    private JObject Metadata { get; }
    private static string Sha(byte[] value)=>Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();
    private static byte[] Jar(string kind) { using MemoryStream data=new();using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true)){using(var entry=zip.CreateEntry("version.json").Open()){byte[] version=Encoding.UTF8.GetBytes("{\"name\":\"1.20.1\"}");entry.Write(version);}using(var entry=zip.CreateEntry(kind+".fixture",CompressionLevel.NoCompression).Open()){byte[] bytes=new byte[4096];new Random(kind=="client"?19:31).NextBytes(bytes);entry.Write(bytes);}}return data.ToArray(); }
    private string Temp(string kind) {string folder=Path.Combine(Root,"temporary",kind+"-"+Interlocked.Increment(ref nextTemp))+Path.DirectorySeparatorChar;Directory.CreateDirectory(folder);return folder;}
    private static void Set(object host,string name,object value)=>Native.Field(host.GetType(),name).SetValue(host,value);
    private void Guard(object host,string name) {FieldInfo field=Native.Field(host.GetType(),name);MethodInfo invoke=field.FieldType.GetMethod("Invoke")!;var parameters=invoke.GetParameters().Select(p=>Expression.Parameter(p.ParameterType)).ToArray();Action fail=()=>{Interlocked.Increment(ref networkForbidden);throw new InvalidOperationException("Unselected HMCL external dependency: "+name);};field.SetValue(host,Expression.Lambda(field.FieldType,Expression.Block(Expression.Invoke(Expression.Constant(fail)),Expression.Default(invoke.ReturnType)),parameters).Compile());}
    internal InstallCase Case(string name,bool hold=false,bool fail=false,bool pauseAdmission=false) {
        string metadata="/"+name+".json";firstMetadataPath??=metadata;string zip=Path.Combine(Root,name+".zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){void Add(string file,string value){using Stream output=archive.CreateEntry(file).Open();output.Write(Encoding.UTF8.GetBytes(value));}Add("modpack.json",new JObject{["name"]=name,["gameVersion"]="1.20.1"}.ToString());Add("minecraft/options.txt","gamma:1.0");Add("minecraft/config/root.txt","actual-hmcl-overrides");Add("minecraft/PCL/Setup.ini","VersionArgumentIndieV2:True\r\nUnknownPackRoot:keep\r\n");}
        var value=new InstallCase(name,Path.Combine(Game,"versions",name),zip,server.Url(metadata),hold,fail,pauseAdmission);
        server.Add(metadata,Encoding.UTF8.GetBytes(Metadata.ToString()),hold&&!fail?value.DownloadEntered:null,hold&&!fail?value.Release:null); return value;
    }
    internal ModLoader.LoaderCombo<string> Start(InstallCase value){current=value;return ModModpack.ModpackInstall(value.Zip);}
    internal void AwaitAdmission(InstallCase value,ModLoader.LoaderCombo<string> root){AdmissionPause pause=value.Admission??throw new InvalidOperationException("Missing owned admission phase.");Until(()=>pause.Scheduled.IsSet,"genuine root Update queued at admission");Native.Check(pause.Claimed==1&&Volatile.Read(ref pause.Producer)?.IsAlive==true&&root.State==ModBase.LoadState.Loading&&ModLoader.LoaderTaskbar.Contains(root)&&!Directory.Exists(value.Folder)&&root.Loaders.Count==2&&root.Loaders.All(loader=>loader.State==ModBase.LoadState.Waiting),"Real duplicate fixture must be Loading/taskbar-registered with its complete original pack/game graph before any target-directory write.");}
    internal void ReleaseAdmission(InstallCase value)=>(value.Admission??throw new InvalidOperationException("Missing owned admission phase.")).Release.Set();
    internal List<ModLoader.LoaderBase> AllNodes(ModLoader.LoaderCombo<string> root){List<ModLoader.LoaderBase> result=new(){root};void Visit(ModLoader.LoaderBase node){Type type=node.GetType();if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(ModLoader.LoaderCombo<>)){foreach(ModLoader.LoaderBase child in (IEnumerable<ModLoader.LoaderBase>)Native.Get(node,"Loaders")!){result.Add(child);Visit(child);}}}Visit(root);return result;}
    private void Observe(ModLoader.LoaderBase node) {if(!observedTasks.TryAdd(node,0))return;Type? type=node.GetType();if(type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(ModLoader.LoaderTask<,>))typeof(PackRootInstallChecksScope).GetMethod(nameof(ObserveTask),BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(type.GetGenericArguments()).Invoke(this,[node]);}
    private void ObserveTask<TInput,TOutput>(ModLoader.LoaderTask<TInput,TOutput> task){FieldInfo field=Native.Field(task.GetType(),"LoadDelegate");var work=(Action<ModLoader.LoaderTask<TInput,TOutput>>)field.GetValue(task)!;field.SetValue(task,new Action<ModLoader.LoaderTask<TInput,TOutput>>(current=>{taskThreads.Add(Thread.CurrentThread);work(current);}));}
    internal void Until(Func<bool> condition,string phase){
        DateTime deadline=DateTime.UtcNow.AddSeconds(12);
        while(true){
            CaptureTransfers();bool complete=condition();
            if(DateTime.UtcNow>=deadline){ReportCompletion("timeout "+phase);throw new TimeoutException("HMCL root phase: "+phase);}
            if(complete)return;
            PumpActiveCondition(condition,deadline);
            Thread.Sleep(2);
        }
    }
    private static void PumpActiveCondition(Func<bool> condition,DateTime deadline){
        // Active HTTP/progress can keep ContextIdle busy. Observe the original
        // data/producer predicate while dispatching real UI callbacks; Finish still
        // performs FileTaskScope's complete UI/IO drain before any receipt is valid.
        Dispatcher dispatcher=Dispatcher.CurrentDispatcher;DispatcherFrame frame=new();
        DispatcherTimer timer=new(DispatcherPriority.Send,dispatcher){Interval=TimeSpan.FromMilliseconds(5)};
        EventHandler observed=(_,_)=>{if(DateTime.UtcNow>=deadline||condition())frame.Continue=false;};
        timer.Tick+=observed;
        DispatcherOperation idle=dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));
        timer.Start();
        try{Dispatcher.PushFrame(frame);}
        finally{timer.Stop();timer.Tick-=observed;if(idle.Status==DispatcherOperationStatus.Pending)idle.Abort();}
    }
    private void CaptureTransfers(){foreach(var download in roots.SelectMany(AllNodes).OfType<ModNet.LoaderDownload>())foreach(var file in download.Files){if(file.Threads is not null)foreach(var part in file.Threads)if(part.Thread is Thread thread)transferThreads.TryAdd(thread,0);}}
    internal bool CaptureHeldTransfer(InstallCase value,ModLoader.LoaderCombo<string> root){CaptureTransfers();string target=Native.Full(Path.Combine(value.Folder,value.Name+".json"));Thread[] held=AllNodes(root).OfType<ModNet.LoaderDownload>().SelectMany(download=>download.Files).Where(file=>Native.Full(file.LocalPath)==target&&file.Threads is not null).SelectMany(file=>file.Threads).Select(part=>part.Thread).OfType<Thread>().Where(thread=>thread.IsAlive).ToArray();foreach(Thread thread in held)transferThreads.TryAdd(thread,0);return held.Length>0;}
    private bool TransientStopped(){CaptureTransfers();
        return admissionPauses.Values.All(pause=>pause.Returned.IsSet&&Volatile.Read(ref pause.Producer)?.IsAlive!=true)&&transferThreads.Keys.All(thread=>!thread.IsAlive)&&taskThreads.All(thread=>!thread.IsAlive)&&Volatile.Read(ref cleanupRunning)==0&&cleanupThreads.All(thread=>ReferenceEquals(thread,Thread.CurrentThread)||!thread.IsAlive)&&Volatile.Read(ref ModNet.NetTaskThreadCount)==0&&networkThreads.Where(thread=>!PackRootInstallChecks.ManagerNames.Contains(thread.Name,StringComparer.Ordinal)).All(thread=>!thread.IsAlive);}
    // Diagnostics observe only this GUID child's synthetic work. They never
    // change the original completion predicate, wait budget, state or counters.
    // Report failures separately so logging cannot replace the primary error.
    private void ReportCompletion(string stage)
    {
        try
        {
            ModNet.LoaderDownload[] downloads=ModNet.NetManager.Tasks.ToArray();
            object ThreadInfo(Thread thread)=>new { Id=thread.ManagedThreadId,Name=thread.Name,Alive=thread.IsAlive,State=thread.ThreadState.ToString() };
            var snapshot=new {
                Kind="HMCL completion observation",Stage=stage,TransientStopped=TransientStopped(),
                FileRemain=Volatile.Read(ref ModNet.NetManager.FileRemain),NetworkWorkers=Volatile.Read(ref ModNet.NetTaskThreadCount),CleanupRunning=Volatile.Read(ref cleanupRunning),
                ManagerTaskCount=downloads.Length,ManagerTasksTerminal=downloads.All(task=>task.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed),
                ManagerTasks=downloads.Take(64).Select(task=>new { task.Name,State=task.State.ToString(),Files=task.Files.Take(8).Select(file=>new { State=file.State.ToString(),Parents=file.Tasks.Count,Registered=ModNet.NetManager.AllFiles.TryGetValue(file.LocalPath,out ModNet.NetFile? registered)&&ReferenceEquals(file,registered),AliveTransfers=file.Threads?.Count(part=>part.Thread?.IsAlive==true)??0 }).ToArray() }).ToArray(),
                Roots=roots.Select(root=>new { root.Name,State=root.State.ToString(),Children=AllNodes(root).Select(node=>new {node.Name,State=node.State.ToString(),Error=node.Error?.ToString()}).Take(32).ToArray(),Target=ObserveTarget(root) }).ToArray(),
                CleanupEvents=cleanupEvents.TakeLast(16).ToArray(),
                AliveTasks=taskThreads.Where(thread=>thread.IsAlive).Distinct().Take(32).Select(ThreadInfo).ToArray(),
                AliveTransfers=transferThreads.Keys.Where(thread=>thread.IsAlive).Take(32).Select(ThreadInfo).ToArray(),
                AliveCleanup=cleanupThreads.Where(thread=>thread.IsAlive).Distinct().Take(32).Select(ThreadInfo).ToArray(),
                NetworkThreads=networkThreads.Distinct().Take(64).Select(ThreadInfo).ToArray(),
                Admission=admissionPauses.Values.Select(pause=>new { pause.Claimed,Scheduled=pause.Scheduled.IsSet,Released=pause.Release.IsSet,Returned=pause.Returned.IsSet,ProducerAlive=Volatile.Read(ref pause.Producer)?.IsAlive==true }).ToArray()
            };
            Console.WriteLine("HMCL SAFE COMPLETION "+System.Text.Json.JsonSerializer.Serialize(snapshot));
        }
        catch(Exception diagnostic)
        {
            try{Console.Error.WriteLine("HMCL completion diagnostic unavailable: "+diagnostic.GetType().Name);}
            catch{/* Keep the original assertion/exception authoritative. */}
        }
    }
    private object ObserveTarget(ModLoader.LoaderBase root){
        try{
            string folder=installHost.CleanupInput(root);var inspection=MmcInstanceInspector.Read(folder);
            return new {Folder=folder,Exists=Directory.Exists(folder),Management=inspection.Management.State.ToString(),inspection.Management.ObservationComplete,inspection.Management.CanUseLegacyBackend,inspection.Error,
                Diagnostics=inspection.Management.Diagnostics.Take(8).ToArray(),Files=Directory.Exists(folder)?Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories).Take(24).Select(file=>new {Path=Path.GetRelativePath(folder,file),Length=new FileInfo(file).Length}).ToArray():null};
        }catch(Exception error){return new {Error=error.ToString()};}
    }
    private sealed class CleanupObserver(BaseLogger original,ConcurrentQueue<string> events):BaseLogger{
        public override void Log(Exception? error,string? message,LogLevel level,LogBehavior behavior,string filePath){
            if(Path.GetFileName(filePath)=="MinecraftInstallWorkflow.cs")events.Enqueue((message??"")+(error is null?"":"\n"+error));
            original.Log(error,message,level,behavior,filePath);
        }
    }
    internal void Settle(ModLoader.LoaderCombo<string> root){Until(()=>AllNodes(root).All(loader=>loader.State!=ModBase.LoadState.Loading)&&TransientStopped(),"real root/cleanup/task/network producer barrier");ReportCompletion("settled "+root.Name);}
    internal void Finish(){foreach(var root in roots)Settle(root);ReportCompletion("before common file-task barrier");files.FinishActualTasks();ReportCompletion("after common file-task barrier");Native.Check(TransientStopped()&&ModNet.NetManager.FileRemain==0&&ModNet.NetManager.Tasks.All(task=>task.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled or ModBase.LoadState.Failed),"HMCL downloader/cleanup is not complete.");Native.Check(StoppedTransfers>0&&TransportUsed,"HMCL actual metadata/client/library/assets HTTP transfer was not observed.");Native.Check(Forbidden==0&&unknownSources.IsEmpty,"HMCL attempted an external dependency, even if caught.");environment.AssertNoForbiddenIo();}
    internal bool ExclusiveFiles(){completeFiles.AddRange(Directory.EnumerateFiles(Game,"*",SearchOption.AllDirectories));foreach(string file in completeFiles){using FileStream exclusive=new(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}return completeFiles.Count>5;}
    internal void StopLoopback()=>server.Dispose();
}
