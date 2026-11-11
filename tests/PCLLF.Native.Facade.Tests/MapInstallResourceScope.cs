using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using PCL;

namespace PCLLF.Native.Facade.Tests;

// Reuses the existing genuine isolated FileTaskScope. These wrappers only
// observe real page threads and forbid unrequested external lookup; transactions,
// worker creation, dispatch, cancellation and native page state remain Main's.
internal sealed class MapInstallResourceScope : IDisposable
{
    private readonly FixtureEnvironment fixture;
    private readonly FileTaskScope tasks;
    private readonly Stack<Action> restore = new();
    private readonly List<object> pages = new();
    private readonly ConcurrentDictionary<Thread, byte> pageThreads = new();
    private readonly HashSet<(IDictionary, string)> cacheKeys = new();
    private int forbiddenOnline, pendingBackground, pageGeneration, schedulingFailed;
    internal readonly string Root;
    internal MapInstallResourceScope(FixtureEnvironment fixture, string name)
    {
        this.fixture = fixture; tasks = new FileTaskScope(fixture);
        try {
            // FileTaskScope has already retained the exact prior instance object.
            // The original standalone page fixture has no selected global instance.
            Native.Field(typeof(PageInstanceLeft), "Instance").SetValue(null, null);
            Root = Path.Combine(tasks.Root, name); Native.Check(!Directory.Exists(Root), "Refusing existing map/resource fixture data."); Directory.CreateDirectory(Root);
            object queue = Native.Field(typeof(ModMain), "HintWaiting").GetValue(null)!;
            Array before = (Array)Native.Call(queue.GetType(), "ToArray", queue)!;
            restore.Push(() => { Native.Call(queue.GetType(), "Clear", queue); foreach (object item in before) Native.Call(queue.GetType(), "Enqueue", queue, item); });
        } catch { Dispose(); throw; }
    }
    internal ModMinecraft.McInstance Instance()
    {
        string folder = Path.Combine(Root, "game", "versions", "example"); Directory.CreateDirectory(folder);
        ModMinecraft.McInstance owner = new(folder) { Version = new PCLCS.McVersion("1.21.11", true) };
        foreach (DictionaryEntry pair in Native.Entries) {
            object entry = pair.Value!; if (Native.Get(entry, "Source")!.ToString() != "Instance") continue;
            IDictionary cache = (IDictionary)Native.Get(entry, "ValueCache")!; Preserve(cache, owner.PathVersion); cache[owner.PathVersion] = Native.Get(entry, "DefaultValue");
        }
        ((IDictionary)Native.Get(Native.Entries["VersionArgumentIndieV2"]!, "ValueCache")!)[owner.PathVersion] = true;
        return owner;
    }
    private void Preserve(IDictionary cache, string key)
    {
        if (!cacheKeys.Add((cache, key))) return; bool existed = cache.Contains(key); object? before = existed ? cache[key] : null;
        restore.Push(() => { if (existed) cache[key] = before; else cache.Remove(key); });
    }
    internal void Track(ModLoader.LoaderBase loader)
        => ((HashSet<ModLoader.LoaderBase>)Native.Get(tasks, "createdRoots")!).Add(loader);
    internal void ObservePage(object page)
    {
        pages.Add(page); Track((ModLoader.LoaderBase)Native.Get(page,"ResourceLoader")!); object controller = Native.Get(page, "Controller")!, host = Native.Get(controller, "Host")!;
        Thread uiThread = Thread.CurrentThread;
        var dispatch = (Action<Action>)Native.Get(host, "Dispatch")!;
        Replace(host, "Dispatch", new Action<Action>(work => {
            if (!ReferenceEquals(Thread.CurrentThread, uiThread)) pageThreads.TryAdd(Thread.CurrentThread, 0);
            dispatch(work);
        }));
        var background = (Action<Action,string>)Native.Get(host, "Background")!;
        Replace(host, "Background", new Action<Action,string>((work,name) => ScheduleBackground(background, work, name)));
        FieldInfo check = Native.Field(host.GetType(), "CheckOnline");
        ParameterExpression[] parameters = check.FieldType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        Action refused = () => { Interlocked.Increment(ref forbiddenOnline); throw new InvalidOperationException("Resource fixture must never perform an online update lookup."); };
        Replace(host,"CheckOnline",Expression.Lambda(check.FieldType,Expression.Block(Expression.Invoke(Expression.Constant(refused)),Expression.Default(check.FieldType.GetMethod("Invoke")!.ReturnType)),parameters).Compile());
    }
    private void ScheduleBackground(Action<Action,string> background, Action work, string name)
    {
        // Register BEFORE the genuine scheduler is called: a queued job may not
        // have a Thread reference yet, even after PageUnloaded clears its UI flags.
        Interlocked.Increment(ref pendingBackground); Interlocked.Increment(ref pageGeneration);
        int entered = 0, reservation = 1;
        try {
            background(() => {
                Interlocked.Exchange(ref entered, 1);
                if (pageThreads.TryAdd(Thread.CurrentThread, 0)) Interlocked.Increment(ref pageGeneration);
                try { work(); }
                finally { Interlocked.Increment(ref pageGeneration); if (Interlocked.Exchange(ref reservation, 0) == 1) Interlocked.Decrement(ref pendingBackground); }
            }, name);
        } catch {
            // A scheduler failure cannot authorize restoration: it might have
            // queued a callback before throwing. Keep every guard installed.
            Interlocked.Exchange(ref schedulingFailed, 1); Interlocked.Increment(ref pageGeneration);
            if (Volatile.Read(ref entered) == 0 && Interlocked.Exchange(ref reservation, 0) == 1) Interlocked.Decrement(ref pendingBackground);
            throw;
        }
    }
    private void Replace(object owner,string name,object value) { FieldInfo field=Native.Field(owner.GetType(),name);object? before=field.GetValue(owner);restore.Push(()=>field.SetValue(owner,before));field.SetValue(owner,value); }
    internal void WaitUntil(Func<bool> predicate)
    {
        DateTime deadline=DateTime.UtcNow.AddSeconds(10); int stable = 0;
        while(true) {
            Native.Check(Volatile.Read(ref schedulingFailed) == 0, "Actual resource scheduler failed; callback ownership is unresolved and context must remain guarded.");
            int before = Volatile.Read(ref pageGeneration);
            Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline);
            Native.Check(Volatile.Read(ref schedulingFailed) == 0, "Actual resource scheduler failed while draining; refusing context restoration.");
            bool quiet = predicate() && Volatile.Read(ref pendingBackground) == 0 && pageThreads.Keys.All(thread=>!thread.IsAlive);
            stable = quiet && Volatile.Read(ref pageGeneration) == before ? stable + 1 : 0;
            if(DateTime.UtcNow>=deadline)throw new TimeoutException("Actual resource page scheduled work/thread/UI completion did not finish.");
            if (stable == 2) return;
            Thread.Sleep(2);
        }
    }
    private void ReleaseNeverStartedPageRegistrations()
    {
        // These original pages deliberately use PageLoaderInit(AutoRun:false).
        // LF LoaderTask.Cancel is a no-op in Waiting; an unstarted page loader
        // cannot satisfy FileTaskScope's real started-root terminal barrier.
        // Remove only this known registration after page/unload/UI quiescence,
        // never turn a Waiting task into a fake Canceled/Finished task.
        var roots = (HashSet<ModLoader.LoaderBase>)Native.Get(tasks, "createdRoots")!;
        foreach (object page in pages)
        {
            ModLoader.LoaderBase loader = (ModLoader.LoaderBase)Native.Get(page, "ResourceLoader")!;
            Thread? thread = (Thread?)Native.Field(loader.GetType(), "LastRunningThread").GetValue(loader);
            if (loader.State != ModBase.LoadState.Waiting || thread is not null) continue;
            Native.Check(page is FrameworkElement view && !view.IsLoaded,
                "A loaded resource page cannot release its unstarted-loader registration.");
            Native.Check(roots.Remove(loader), "Known unstarted page loader lost its fixture registration.");
            Console.WriteLine("DIAG map/resource released never-started page registration: " + loader.Name +
                "; State=" + loader.State + "; LastRunningThread=null; IsLoaded=false; actual started loaders remain tracked.");
        }
    }
    public void Dispose()
    {
        try {
            foreach(object page in pages) { Native.Call(page.GetType(),"PageUnloaded",page); ((MyPageRight)page).PageOnForceExit(); }
            WaitUntil(()=>pages.All(page=>{object controller=Native.Get(page,"Controller")!;return !(bool)Native.Get(controller,"Busy")!&&!(bool)Native.Get(controller,"Checking")!&&Native.Get(controller,"Operation") is null&&Native.Get(controller,"OnlineCancellation") is null;}));
            ReleaseNeverStartedPageRegistrations();
            tasks.FinishActualTasks(); Native.Check(Volatile.Read(ref forbiddenOnline)==0,"Resource guard observed an unintended online lookup, even if Main caught its error."); fixture.AssertNoForbiddenIo();
            while(restore.Count>0)restore.Pop()();tasks.Dispose();
        }
        catch(Exception error) { Native.Call(typeof(FileTaskScope),"FailClosed",tasks,"Map/resource completion barrier failed before restoring context.",error);throw; }
    }
}
