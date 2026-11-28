using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS;
using ModeHost = PCLCS.ModpackModeHost<System.Windows.FrameworkElement, PCL.ModMinecraft.McFolder, PCL.ModMinecraft.McInstance>;

namespace PCLLF.Native.Facade.Tests;

// LF f4/main50 original fixtures; actual Main types/entry points, no CE policy.
internal static partial class ModpackModeChecks {
    private static readonly Type Mode = Native.Type("PCL.ModModpackMode");
    private static object? Call(string name, params object?[] arguments) => Native.Call(Mode, name, null, arguments);
    private static bool Enabled => (bool)Native.Property(Mode, "Enabled").GetValue(null)!;
    private static void M(int line, bool condition) => ModpackModeSourceChecks.Check("Test-ModpackMode.ps1", line, condition);
    private static void F(int line, bool condition) => ModpackModeSourceChecks.Check("Test-ModpackFolderScope.ps1", line, condition);
    private static void E(int line, bool condition) => ModpackModeSourceChecks.Check("Test-ExportVersionIsolation.ps1", line, condition);
    private static T Part<T>(FrameworkElement owner, string name) where T : class => owner.FindName(name) as T ?? throw new MissingMemberException(owner.GetType().FullName, name);
    private static object Entry(string key) => Native.Entries[key] ?? throw new MissingMemberException(key);
    private static IDictionary Cache(string key) => (IDictionary)Native.Get(Entry(key), "ValueCache")!;
    private static object? Cached(string key) => Cache(key)[""];
    private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    internal static void Run(FixtureEnvironment fixture) {
        VerifyChild(fixture);
        ModpackModeSourceChecks.Reset();
        using Scope scope = new(fixture);
        var views = CheckUi(scope); CheckExportMode(scope, views.Export); CheckComponents(scope, views.Overview);
        CheckFolders(scope); CheckIsolation(scope, views.Export);
        scope.DrainValidation(); scope.AssertIdle(); fixture.AssertNoForbiddenIo(); ModpackModeSourceChecks.Complete();
    }

    private static void VerifyChild(FixtureEnvironment fixture) {
        string[] args = Environment.GetCommandLineArgs(); int option = Array.IndexOf(args,"--fixture");
        Native.Check(OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && System.Windows.Application.Current.Dispatcher.CheckAccess(), "Mode fixture requires genuine Windows STA.");
        Native.Check(Assembly.GetEntryAssembly() == typeof(ModpackModeChecks).Assembly && typeof(ModpackModeChecks).Assembly.GetName().Name == "PCLLF.Native.Facade.Tests" && args.Contains("--isolated",StringComparer.Ordinal) && option >= 0 && option + 1 < args.Length, "Mode cleanup requires the genuine isolated Facade child.");
        string actual = Native.Full(args[option + 1]), prefix = Native.Full(Path.GetTempPath()) + Path.DirectorySeparatorChar;
        Native.Check(actual.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(actual),"^PclNativeFacade-[a-f0-9]{32}$") && Native.Full(fixture.Root) == actual && Native.Full(AppContext.BaseDirectory) == Native.Full(Path.Combine(actual,"output")), "Mode child/output/GUID root mismatch.");
    }
    private static void FailClosed(FixtureEnvironment fixture, string reason, Exception? failure = null) {
        VerifyChild(fixture); Console.Error.WriteLine("FAIL modpack-mode cleanup before any guard/cache restoration: " + reason + " " + failure); Console.Error.Flush(); Console.Out.Flush();
        Environment.Exit(96); throw new InvalidOperationException(reason,failure);
    }
    private sealed class Scope : IDisposable {
        private readonly Stack<Action> restore = new();
        private readonly HashSet<string> globals = new(StringComparer.Ordinal), caches = new(StringComparer.Ordinal);
        private readonly List<FrameworkElement> pages = new();
        private readonly List<ModLoader.LoaderBase> loaders = new();
        private readonly FixtureEnvironment fixture;
        private readonly PropertyInfo animation = Native.Property(Native.Type("PCL.ModAnimation"), "AniControlEnabled");
        private readonly RedoableWorker<Java> java = LauncherInstanceContext.CurrentJavaWorker;
        private readonly object producerGate = new();
        private readonly HashSet<Thread> producers = new();
        private readonly List<DispatcherOperation> producerUi = new();
        private readonly Dictionary<int,ThreadLease> leases = new();
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private bool restored;
        private readonly bool verifiedChild;
        private int forbidden;
        internal readonly string Root, Launcher, AppData;
        internal readonly ModeHost Host;
        internal Scope(FixtureEnvironment fixture) {
            VerifyChild(fixture); verifiedChild = true;
            this.fixture = fixture; Root = Path.Combine(fixture.Data, "modpack-mode"); Launcher = Path.Combine(Root, "other-launcher") + Path.DirectorySeparatorChar; AppData = Path.Combine(Root, "appdata") + Path.DirectorySeparatorChar;
            Native.Check(!Directory.Exists(Root), "Refusing existing modpack-mode fixture."); Directory.CreateDirectory(Launcher); Directory.CreateDirectory(AppData);
            object service = Native.Field(Mode, "Service").GetValue(null)!; Host = (ModeHost)Native.Get(service, "host")!;
            Native.Check(service.GetType().Assembly.GetName().Name == "PCLLF.WPF.Core" && service.GetType().GetGenericArguments().SequenceEqual(new[] { typeof(FrameworkElement), typeof(ModMinecraft.McFolder), typeof(ModMinecraft.McInstance) }), "Mode fixture is not the genuine Core/Main closure.");
            try {
                CaptureValidationThreads(false);
                DispatcherHookEventHandler posted = (_,args) => { Thread thread = Thread.CurrentThread; if (!IsValidation(thread.Name)) return; lock (producerGate) { producers.Add(thread); producerUi.Add(args.Operation); } };
                dispatcher.Hooks.OperationPosted += posted; restore.Push(() => dispatcher.Hooks.OperationPosted -= posted);
                Replace(Host, "BaseFolder", new Func<string>(() => Launcher));
                Replace(Host, "Message", new Func<ModpackMessage,int>(_ => Forbidden<int>("unexpected mode confirmation")));
                Replace(Host, "RunInNewThread", new Action<Action,string>((_,_) => Forbidden<int>("unexpected mode scope worker")));
                SaveField(service, "CompactSettings"); Native.Set(service, "CompactSettings", null);
                foreach (string name in new[] { "FrmMain", "FrmSetupLeft", "FrmLaunchLeft", "FrmInstanceLeft", "FrmInstanceOverall", "FrmInstanceMod", "FrmInstanceModDisabled", "FrmInstanceSetup", "FrmSetupLaunch", "FrmSetupSystem", "FrmSelectLeft", "FrmSelectRight", "FrmInstanceExport", "FrmDownloadModify" }) Global(name, null);
                SaveStatic(typeof(LauncherInstanceContext), "Instance"); LauncherInstanceContext.Instance = null!;
                SaveStatic(typeof(ModMinecraft), "McFolderList");
                SaveStatic(typeof(ModMinecraft), "_McInstanceSelected"); Native.State(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, null);
                SaveStatic(typeof(ModMinecraft), "_McFolderSelected"); Native.State(typeof(ModMinecraft), "_McFolderSelected").SetValue(null, Path.Combine(Launcher, ".minecraft") + Path.DirectorySeparatorChar);
                // Only the existing platform path input is replaced. Even an
                // incidental real Main folder getter must stay inside the GUID root.
                object minecraft = Native.Field(typeof(ModMinecraft), "Service").GetValue(null)!;
                object minecraftHost = minecraft.GetType().GetFields(Native.Flags).Single(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(LegacyMinecraftHost<,,,,>)).GetValue(minecraft)!;
                Replace(minecraftHost, "PackFolder", new Func<string>(() => (string)Native.Static("PCL.MinecraftFolderPolicy", "PackFolder", Launcher)!));
                SaveStatic(typeof(LauncherAppearanceSettings), "<HiddenForceShow>k__BackingField");
                PropertyInfo before = Native.Property(Native.Type("PCL.InstanceClonerUi"), "BeforeMinecraftChangeHandler"); object? previous = before.GetValue(null); restore.Push(() => before.SetValue(null, previous));
                int oldAnimation = (int)animation.GetValue(null)!; restore.Push(() => animation.SetValue(null, oldAnimation)); animation.SetValue(null, checked(oldAnimation + 1));
                Native.Check(!java.Running, "An existing Java worker is running before the mode fixture."); ObserveEvent(java, "Stopped"); ObserveEvent(java, "Started");
                ObserveLoader(ModMinecraft.McFolderListLoader); ObserveLoader(LocalResourceLoaders.LocalResourceLoader); ObserveLoader(LocalResourceLoaders.LocalResourceOnlineLoader);
                var ini = (ConcurrentDictionary<string,ConcurrentDictionary<string,string>>)Native.Field(Native.Type("PCL.ModBase"), "IniCache").GetValue(null)!;
                restore.Push(() => { foreach (string key in ini.Keys.Where(key => key.StartsWith(Native.Full(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray()) Native.Static("PCL.ModBase", "IniClearCache", key); });
                SetCache("LivingModpackMode", false); SetCache("LivingModpackExported", false); SetCache("LaunchArgumentIndieV2", 3);
            } catch (Exception initialization) {
                try { DrainValidation(); AssertIdle(); } catch (Exception cleanup) { FailClosed(fixture,"Constructor failure did not prove all real validation producers and UI tails idle.",new AggregateException(initialization,cleanup)); }
                Restore(); throw;
            }
        }
        private T Forbidden<T>(string name) { Interlocked.Increment(ref forbidden); throw new InvalidOperationException("Forbidden actual mode fixture operation: " + name); }
        internal void Replace(object target, string name, object value) { FieldInfo field = Native.Field(target.GetType(), name); object? previous = field.GetValue(target); restore.Push(() => field.SetValue(target, previous)); field.SetValue(target, value); }
        private void SaveField(object target, string name) { FieldInfo field = Native.Field(target.GetType(), name); object? previous = field.GetValue(target); restore.Push(() => field.SetValue(target, previous)); }
        private void SaveStatic(Type type, string name) { var field = Native.State(type, name); object? previous = field.GetValue(null); restore.Push(() => field.SetValue(null, previous)); }
        internal void Global(string name, object? value) { FieldInfo field = Native.Field(typeof(ModMain), name); if (globals.Add(name)) { object? previous = field.GetValue(null); restore.Push(() => field.SetValue(null, previous)); } field.SetValue(null, value); }
        internal void SetCache(string key, object value, string path = "") {
            IDictionary cache = Cache(key); if (caches.Add(key + "\0" + path)) { bool had = cache.Contains(path); object? old = cache[path]; restore.Push(() => { if (had) cache[path] = old; else cache.Remove(path); }); } cache[path] = value;
        }
        internal T Keep<T>(T page) where T : FrameworkElement { pages.Add(page); return page; }
        internal FrameworkElement New(string name) => Keep((FrameworkElement)Activator.CreateInstance(Native.Type("PCL." + name), true)!);
        internal void ObserveLoader(ModLoader.LoaderBase loader) { Native.Check(loader.State.ToString() != "Loading", "A pre-existing loader is busy."); loaders.Add(loader); foreach (string name in new[] { "PreviewFinish", "OnStateChangedUi", "LoadingStateChanged", "ProgressChanged" }) ObserveEvent(loader, name); }
        private void ObserveEvent(object target, string name) => ObserveEvent(target.GetType(), target, name);
        private void ObserveEvent(Type type, object? target, string name) {
            EventInfo evt = type.GetEvent(name, Native.Flags) ?? throw new MissingMemberException(type.FullName, name); FieldInfo field = Native.Field(evt.DeclaringType!, name);
            Delegate[] before = (field.GetValue(target) as Delegate)?.GetInvocationList() ?? [];
            restore.Push(() => {
                List<Delegate> fresh = new((field.GetValue(target) as Delegate)?.GetInvocationList() ?? []);
                foreach (Delegate prior in before) Native.Check(fresh.Remove(prior), "Mode fixture removed a pre-existing event subscriber: " + name);
                foreach (Delegate added in fresh) evt.RemoveEventHandler(target, added);
                Native.Check(((field.GetValue(target) as Delegate)?.GetInvocationList() ?? []).SequenceEqual(before), "Mode fixture event subscribers were not restored: " + name);
            });
        }
        private static bool IsValidation(string? name) => name is "DelayedValidate Change" or "DelayedValidate Text";
        private sealed class ThreadLease : WaitHandle { internal ThreadLease(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
        [DllImport("kernel32.dll",SetLastError = true)] private static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern int GetThreadDescription(SafeWaitHandle thread, out IntPtr description);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private void CaptureValidationThreads(bool adopt = true) {
            // .NET10 Thread.Name is propagated to the Windows thread description.
            // Observe the actual pre-post sleep/UiWait producers, not a mirror
            // counter or a replacement workload. A signalled HANDLE proves exit.
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessThread thread in process.Threads) {
                using (thread) {
                    int id = thread.Id; SafeWaitHandle handle = OpenThread(0x00100800,false,checked((uint)id));
                    if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); if (error == 87) continue; throw new Win32Exception(error,"Querying actual validation producer failed."); }
                    ThreadLease lease = new(handle); IntPtr pointer = IntPtr.Zero;
                    try {
                        int result = GetThreadDescription(handle,out pointer); if (result < 0) { if (lease.WaitOne(0)) continue; Marshal.ThrowExceptionForHR(result); }
                        string? name = Marshal.PtrToStringUni(pointer); if (!IsValidation(name)) continue;
                        if (!adopt) { Native.Check(lease.WaitOne(0),"A pre-existing validation producer prevents ownership of this isolated mode fixture."); continue; }
                        if (leases.TryGetValue(id,out ThreadLease? previous)) { if (!previous.WaitOne(0)) continue; previous.Dispose(); }
                        leases[id] = lease; lease = null!; // Retain this exact thread object even if the numeric ID is later reused.
                    } finally { if (pointer != IntPtr.Zero) LocalFree(pointer); lease?.Dispose(); }
                }
            }
        }
        private bool ValidationExited() {
            CaptureValidationThreads();
            lock (producerGate) return leases.Values.All(handle => handle.WaitOne(0)) && producers.All(thread => !thread.IsAlive) && producerUi.All(operation => operation.Status == DispatcherOperationStatus.Completed);
        }
        private void Pump(Func<bool> done, string reason, int timeout = 5000) {
            Native.Check(verifiedChild, "An unverified process cannot arm the mode watchdog.");
            Stopwatch budget = Stopwatch.StartNew(); int outcome = 0; // 0=running, 1=finished before deadline, 2=expired
            bool complete = false; Exception? failure = null; DispatcherFrame frame = new();
            void Expire() {
                Interlocked.Exchange(ref outcome,2);
                // This bool was established on the real STA before any guard
                // mutation. Do not call STA-only VerifyChild from a timer.
                // Exit directly: a blocked UI callback or full output pipe must
                // not postpone the verified child's absolute cleanup deadline.
                if (verifiedChild) Environment.Exit(96);
            }
            void Sample() {
                if (budget.ElapsedMilliseconds >= timeout || Volatile.Read(ref outcome) == 2) { Expire(); return; }
                try { complete = done(); } catch (Exception error) { failure ??= error; }
                if (budget.ElapsedMilliseconds >= timeout || Volatile.Read(ref outcome) == 2) { Expire(); return; }
                if (complete || failure is not null) frame.Continue = false;
            }
            DispatcherTimer observer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) }; EventHandler tick = (_,_) => Sample(); observer.Tick += tick;
            int remaining = Math.Max(0, timeout - checked((int)budget.ElapsedMilliseconds));
            // Hard watchdog is independent of every Dispatcher priority and of
            // the callback being sampled. Its only target is this verified child.
            using System.Threading.Timer deadline = new(_ => { if (Interlocked.CompareExchange(ref outcome,2,0) == 0 && verifiedChild) Environment.Exit(96); },null,remaining,Timeout.Infinite);
            try { observer.Start(); Dispatcher.PushFrame(frame); }
            finally {
                // A late done=true or a pending timer which has not received a
                // worker yet can never convert an expired budget into success.
                if (budget.ElapsedMilliseconds >= timeout || Interlocked.CompareExchange(ref outcome,1,0) == 2) Expire();
                deadline.Dispose(); observer.Stop(); observer.Tick -= tick;
            }
            if (budget.ElapsedMilliseconds >= timeout || Volatile.Read(ref outcome) == 2) Expire();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw(); Native.Check(complete,"Bounded actual mode wait failed: " + reason);
        }
        internal void DrainValidation() {
            int barriers = 0; DispatcherOperation? tail = null;
            try {
                Pump(() => {
                    if (!ValidationExited()) { barriers = 0; return false; }
                    if (barriers == 2) return true;
                    if (tail is null) tail = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(() => { tail = null; if (ValidationExited()) barriers++; else barriers = 0; }));
                    return false;
                },"real validation thread HANDLE exit, managed producer joins, completion and two UI tails");
            } finally { if (tail?.Status == DispatcherOperationStatus.Pending) tail.Abort(); }
        }
        internal void AssertIdle() { Native.Check(Volatile.Read(ref forbidden) == 0 && !java.Running && loaders.All(loader => loader.State.ToString() != "Loading") && pages.All(page => !page.IsLoaded) && ValidationExited(), "Mode fixture started forbidden I/O, a worker/loader/loaded page or left an actual validation producer/UI callback."); fixture.AssertNoForbiddenIo(); }
        private void Restore() {
            if (restored) return; restored = true;
            List<Exception> failures = new(); while (restore.TryPop(out Action? action)) { try { action(); } catch (Exception failure) { failures.Add(failure); } }
            foreach (ThreadLease lease in leases.Values) lease.Dispose(); leases.Clear(); pages.Clear();
            if (failures.Count > 0) throw new AggregateException("Mode scope restoration failed after proven producer exit.",failures);
        }
        public void Dispose() {
            if (restored) return;
            try { DrainValidation(); AssertIdle(); }
            catch (Exception failure) { FailClosed(fixture,"Real mode producers/UI tails did not finish; all guards and roots retained until isolated child exit.",failure); }
            Restore(); // Never restore from a finally while a real producer can still run.
        }
    }
}
