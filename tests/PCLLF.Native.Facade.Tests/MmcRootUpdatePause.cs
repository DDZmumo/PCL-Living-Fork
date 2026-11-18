using PCL;
using PCLCS.Loaders;

namespace PCLLF.Native.Facade.Tests;

// Pause only the existing shared scheduling boundary. The genuine Update and its producer
// observer are preserved and run after cancellation; this does not replace worker algorithms.
internal sealed class MmcRootUpdatePause:IDisposable {
    private readonly LegacyLoaderHost<ModLoader.LoaderBase,ModBase.LoadState,MyLoading.MyLoadingState> host;
    private readonly Action<Action> original;
    private readonly ManualResetEventSlim returned=new();
    private Action? captured;
    internal bool Captured=>captured is not null;
    internal MmcRootUpdatePause(Func<ModLoader.LoaderCombo<string>?> selected){
        host=(LegacyLoaderHost<ModLoader.LoaderBase,ModBase.LoadState,MyLoading.MyLoadingState>)Native.Field(typeof(ModLoader),"NativeLoaderHost").GetValue(null)!;
        original=host.RunInThread;
        host.RunInThread=work=>{
            Type? type=work.Target?.GetType();
            var owner=type?.IsGenericType==true&&type.GetGenericTypeDefinition()==typeof(LegacyLoaderComboEngine<,,,,>)&&work.Method.Name=="Update"?Native.Get(work.Target!,"owner"):null;
            if(captured is null&&owner is not null&&ReferenceEquals(owner,selected())){captured=work;return;}
            original(work);
        };
    }
    internal void Release(){var work=captured??throw new InvalidOperationException("No genuine MMC root Update was captured.");original(()=>{try{work();}finally{returned.Set();}});}
    internal void AwaitReturn(){DateTime deadline=DateTime.UtcNow.AddSeconds(10);while(!returned.IsSet){Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline);if(DateTime.UtcNow>=deadline)throw new TimeoutException("Canceled real MMC root Update did not return.");Thread.Sleep(2);}}
    public void Dispose(){host.RunInThread=original;returned.Dispose();}
}
