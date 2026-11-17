using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using PCLCS.Loaders;
using PCLCS.Network;
using CountRuntime=PCLCS.Network.LegacyNetworkRuntime<PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.Source,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.Part,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.File,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.Download,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.Manager,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.Loader,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.NetState,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.LoadState,PCLLF.Native.Facade.Tests.PackRootInstallChecksCounter.UiState>;

namespace PCLLF.Native.Facade.Tests;

// These are typed host data for the ACTUAL production Core manager/file/loader
// methods, not an installer or downloader replacement. No permanent manager
// bodies or HTTP run in this narrow ownership contract. The existing HMCL
// scenarios below still run genuine Main pack/game/HTTP/cleanup in full.
internal static class PackRootInstallChecksCounter
{
    internal enum NetState { Checking=-1,Waiting=0,Finished=5,Error=6 }
    internal enum LoadState { Waiting,Loading,Finished,Failed,Canceled }
    internal enum UiState { Stop,Run,Error }
    internal sealed class Source:LegacyNetSourceData<Part> { }
    internal sealed class Part:LegacyNetThreadData<File,Source,Part,NetState> { public Part():base(()=>0,NetState.Waiting){} }
    internal sealed class File:LegacyNetFile<Source,Part,File,Download,Manager,Loader,NetState,LoadState,UiState>
    { internal File(CountRuntime runtime,string path):base(runtime,["http://127.0.0.1:1/owned-no-request"],path){} }
    internal sealed class Manager(CountRuntime runtime):LegacyNetManager<Source,Part,File,Download,Manager,Loader,NetState,LoadState,UiState>(runtime) { }
    internal abstract class Loader:LegacyLoaderBase<Loader,LoadState,UiState>
    {
        private static int nextUuid;
        protected Loader():base(new(){Waiting=LoadState.Waiting,Loading=LoadState.Loading,Finished=LoadState.Finished,Failed=LoadState.Failed,Canceled=LoadState.Canceled,UiStop=UiState.Stop,UiRun=UiState.Run,UiError=UiState.Error,NewUuid=()=>Interlocked.Increment(ref nextUuid),RunInUi=action=>action(),DebugFinishDelay=()=>false}){}
        protected override void ConnectUiHandler(Action<Loader,LoadState,LoadState> handler){} protected override void ConnectThreadHandler(Action<Loader,LoadState,LoadState> handler){}
        protected override void RaiseStateUi(Loader loader,LoadState next,LoadState previous){} protected override void RaiseStateThread(Loader loader,LoadState next,LoadState previous){}
        protected override void RaiseLoadingState(UiState next,UiState previous){} protected override void RaiseProgress(double next,double previous){}
        protected override void RaisePreviewFinishCore(Loader loader){} protected override void ApplyFileWorkloadGuard(Func<Action,Action> wrap){}
    }
    internal sealed class Download:Loader,ILegacyNetworkDownload<File>
    {
        private readonly CountRuntime runtime;
        internal Download(CountRuntime runtime,params File[] files){this.runtime=runtime;runtime.InitializeDownload(this,"owned counter contract",files);}
        public ConcurrentQueue<File> CoreFiles {get;set;}=new(); public LegacyNetworkDownloadRecord CoreRecord {get;}=new(); public int FailCount {get;set;}
        public override void Start(object? input=null,bool force=false)=>runtime.StartDownload(this,input,force);
        public override void Cancel()=>runtime.CancelDownload(this); public override void Failed(Exception error)=>runtime.FailDownload(this,error);
        public void CorePreviewFinish(){} public void RefreshStat(){} public void OnFileFinish(File file)=>runtime.OnDownloadFileFinish(this,file); public void OnFileFail(File file)=>runtime.OnDownloadFileFail(this,file); public void OnFail(List<Exception> errors)=>runtime.OnDownloadFail(this,errors);
    }
    private sealed class Context:IDisposable
    {
        internal readonly string Root;
        internal readonly CountRuntime Runtime;
        internal readonly Manager Initial;
        internal Manager Selected;
        private int uuid;
        private readonly ConcurrentBag<Thread> finite=new();
        internal Context(string parent)
        {
            Root=Path.Combine(parent,"counter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);
            var host=new LegacyNetworkHost<File,Download,Manager,Loader>{Clock=()=>0,NewUuid=()=>Interlocked.Increment(ref uuid),SignCdn=source=>source,WrapRedirects=handler=>handler,Manager=()=>Selected!,TempFolder=()=>Root+Path.DirectorySeparatorChar};
            host.RunNewThread=(work,name,priority)=>{
                if(name is "NetManager ThreadStarter 0" or "NetManager ThreadStarter 1" or "NetManager StatRefresher")
                    return new Thread(()=>{}){Name=name}; // Real Start registration, controlled non-running perpetual scheduler boundary.
                Thread thread=new(()=>work()){Name=name,Priority=priority};finite.Add(thread);thread.Start();return thread;
            };
            Runtime=new(host);Initial=new(Runtime);Selected=Initial;
        }
        internal File NewFile(string? path=null)=>new(Runtime,path??Path.Combine(Root,"file-"+Interlocked.Increment(ref uuid)+".json"));
        internal Download Owner(params File[] files)=>new(Runtime,files);
        internal static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Actual Core counter contract: "+message);}
        internal void Wait(Func<bool> done,string phase)
        {
            Stopwatch watch=Stopwatch.StartNew();while(!done()){if(watch.ElapsedMilliseconds>=2000)throw new TimeoutException("Actual Core counter phase: "+phase);Thread.Sleep(1);}
        }
        public void Dispose()
        {
            Stopwatch watch=Stopwatch.StartNew();foreach(Thread thread in finite)Check(thread.Join(Math.Max(0,2000-(int)watch.ElapsedMilliseconds)),"finite initialization thread did not return");
            Check(Initial.FileRemain==0&&Selected.FileRemain==0,"private counter context still owns a count");
            ((IDisposable)typeof(CountRuntime).GetField("ThreadClient",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Runtime)!).Dispose();
            Directory.Delete(Root,true);
        }
    }
    internal static void Run(string parent)
    {
        int passed=0;
        void Case(string name,Action<Context> body)
        {
            Context context=new(parent);Exception? primary=null;
            try{body(context);}
            catch(Exception error){primary=error;throw;}
            finally
            {
                try{context.Dispose();}
                catch(Exception cleanup)
                {
                    if(primary is null)throw;
                    try{Console.Error.WriteLine("Counter context cleanup after primary "+primary.GetType().Name+": "+cleanup);}
                    catch{/* Keep the original failed production assertion. */}
                }
            }
            passed++;Console.WriteLine("PASS actual Core count ownership "+name);
        }
        Case("unregistered-placeholder",context=>{
            File file=context.NewFile();Download owner=context.Owner(file);owner.Cancel();
            Context.Check(context.Initial.FileRemain==0&&context.Initial.Tasks.IsEmpty&&context.Initial.AllFiles.IsEmpty&&file.State==NetState.Error&&owner.State==LoadState.Canceled,"never-registered cancellation decremented manager");owner.Cancel();file.Finish();Context.Check(context.Initial.FileRemain==0,"repeated terminal operations changed unregistered count");
        });
        Case("registered-finish-once",context=>{
            File file=context.NewFile();Download owner=context.Owner(file);Context.Check(context.Initial.Start(owner).Single()==file&&context.Initial.FileRemain==1,"real Start did not register one file");file.Finish();owner.Cancel();file.Finish();Context.Check(context.Initial.FileRemain==0&&owner.State==LoadState.Finished,"registered finish/cancel released more or less than once");
        });
        Case("registered-cancel-once",context=>{
            File file=context.NewFile();Download owner=context.Owner(file);context.Initial.Start(owner);owner.Cancel();owner.Cancel();file.Finish();Context.Check(context.Initial.FileRemain==0&&file.State==NetState.Error,"registered cancellation did not release exactly once");
        });
        Case("file-failure-count",context=>{
            void Fail(File file,Exception error)
            {
                // The genuine file failure entry consumes the source failure
                // data normally recorded by its transport before calling Fail.
                foreach(Source source in file.Sources){source.Ex=error;source.IsFailed=true;}
                file.GetType().BaseType!.GetMethod("Fail",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(file,[error]);
            }
            File registered=context.NewFile();Download owner=context.Owner(registered);context.Initial.Start(owner);Exception failure=new InvalidOperationException("owned actual file failure");Fail(registered,failure);
            Context.Check(context.Initial.FileRemain==0&&registered.State==NetState.Error&&owner.State==LoadState.Failed&&ReferenceEquals(owner.Error?.InnerException,failure),"real registered file failure did not return its count and exact error");
            File placeholder=context.NewFile();Fail(placeholder,new InvalidOperationException("owned unregistered file failure"));Context.Check(context.Initial.FileRemain==0&&placeholder.State==NetState.Error,"unregistered real file failure decremented another count");
        });
        Case("shared-parents",context=>{
            File first=context.NewFile();Download a=context.Owner(first),b=context.Owner(context.NewFile(first.LocalPath));context.Initial.Start(a);Context.Check(context.Initial.Start(b).Count==0&&ReferenceEquals(b.CoreFiles.Single(),first)&&context.Initial.FileRemain==1,"same-path parents did not share actual file/count");a.Cancel();Context.Check(context.Initial.FileRemain==1&&first.State==NetState.Checking&&first.Tasks.Count==1,"first parent stole shared count");b.Cancel();Context.Check(context.Initial.FileRemain==0,"last parent did not release shared count");
        });
        Case("replacement-slot",context=>{
            File previous=context.NewFile();Download old=context.Owner(previous);context.Initial.Start(old);previous.Finish();File next=context.NewFile(previous.LocalPath);Download owner=context.Owner(next);context.Initial.Start(owner);previous.Finish();old.Cancel();Context.Check(context.Initial.FileRemain==1&&ReferenceEquals(context.Initial.AllFiles[next.LocalPath],next),"old terminal file released replacement slot");owner.Cancel();Context.Check(context.Initial.FileRemain==0,"replacement slot did not return count");
        });
        Case("actual-manager-owner",context=>{
            Manager receiver=new(context.Runtime);context.Selected=receiver;File file=context.NewFile();Download owner=context.Owner(file);context.Selected=context.Initial;receiver.Start(owner);Context.Check(receiver.FileRemain==1&&context.Initial.FileRemain==0,"registration did not belong to actual manager receiver");owner.Cancel();Context.Check(receiver.FileRemain==0&&context.Initial.FileRemain==0,"release re-resolved Host.Manager instead of captured owner");
        });
        Case("late-canceled-owner",context=>{
            File file=context.NewFile();Download owner=context.Owner(file);owner.Cancel();Context.Check(context.Initial.Start(owner).Count==0&&context.Initial.FileRemain==0&&context.Initial.Tasks.IsEmpty&&context.Initial.AllFiles.IsEmpty,"late initializer registered an already canceled owner");
        });
        Case("register-versus-cancel",context=>{
            for(int i=0;i<32;i++){File file=context.NewFile();Download owner=context.Owner(file);Race(()=>context.Initial.Start(owner),owner.Cancel);Context.Check(context.Initial.FileRemain==0&&owner.State==LoadState.Canceled&&file.State==NetState.Error,"registration/cancel race leaked or underflowed count");}
        });
        Case("finish-versus-cancel",context=>{
            for(int i=0;i<32;i++){File file=context.NewFile();Download owner=context.Owner(file);context.Initial.Start(owner);Race(()=>file.Finish(),owner.Cancel);Context.Check(context.Initial.FileRemain==0&&file.State is NetState.Finished or NetState.Error,"finish/cancel race released other than once");}
        });
        if(OperatingSystem.IsWindows())Case("old-reset-tail-fresh-registration",context=>{
            File file=context.NewFile();Download old=context.Owner(file);context.Initial.Start(old);Manager receiver=new(context.Runtime);Thread? cancel=null;
            Monitor.Enter(file.LockChain);
            try{
                cancel=new Thread(old.Cancel);cancel.Start();context.Wait(()=>file.State==NetState.Error,"old cancellation reached original Reset with its captured count");
                Context.Check(context.Initial.FileRemain==1,"old cancellation released before its original Reset");context.Selected=receiver;
                // NetFile is a single-operation input. A later operation creates a
                // fresh file for this same target while the old Reset tail is held.
                File fresh=context.NewFile(file.LocalPath);Download next=context.Owner(fresh);next.Start();
                context.Wait(()=>receiver.FileRemain==1&&fresh.State==NetState.Waiting,"actual new initialization registers a fresh same-path file");
                Context.Check(!ReferenceEquals(file,fresh)&&ReferenceEquals(next.CoreFiles.Single(),fresh)&&file.State==NetState.Error,"new registration revived the sealed old NetFile");
                Monitor.Exit(file.LockChain);
                try{Context.Check(cancel.Join(2000),"old cancellation tail did not return");Context.Check(context.Initial.FileRemain==0&&receiver.FileRemain==1,"old Reset tail released later registration");next.Cancel();Context.Check(receiver.FileRemain==0,"new registration failed its own cancellation");}
                finally{Monitor.Enter(file.LockChain);}
            }finally{Monitor.Exit(file.LockChain);if(cancel is not null)Context.Check(cancel.Join(2000),"old cancel still alive after release");}
        });
        else Console.WriteLine("PENDING actual Core old-reset-tail-fresh-registration: genuine initializer requires Windows path semantics; not counted as Linux pass.");
        Console.WriteLine("PASS actual Core count ownership contracts: "+passed+". Separate real HMCL Main/game/HTTP cases and exact zero/26-ID receipt remain required.");
    }
    private static void Race(Action first,Action second)
    {
        using ManualResetEventSlim start=new();ConcurrentQueue<Exception> errors=new();
        Thread Make(Action work)=>new(()=>{try{if(!start.Wait(2000))throw new TimeoutException("counter race was not released");work();}catch(Exception error){errors.Enqueue(error);}});
        Thread a=Make(first),b=Make(second);a.Start();b.Start();start.Set();Stopwatch watch=Stopwatch.StartNew();
        Context.Check(a.Join(2000)&&b.Join(Math.Max(0,2000-(int)watch.ElapsedMilliseconds)),"actual counter race threads did not both return");if(!errors.IsEmpty)throw new AggregateException(errors);
    }
}
