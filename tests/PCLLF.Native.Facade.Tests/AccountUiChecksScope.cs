using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using PCLLF.Presentation.Accounts;
using AccountHost = PCLLF.Presentation.Accounts.LegacyLoginHost<PCL.ModLaunch.McLoginLegacy, PCL.ModLaunch.McLoginMs, PCL.ModLaunch.McLoginServer>;

namespace PCLLF.Native.Facade.Tests;

// Only original platform dispatch, HTTP and Registry primitives are replaced.
// The actual Main page/controller/task, LF DTO, Settings cache/crypto/schema,
// account transaction and page-navigation delegates remain the subjects.
internal sealed class AccountUiChecksScope : IDisposable
{
    internal enum Scenario { None, Success, Failure, Cancel }
    internal enum Checkpoint { Constructing, Ready, Navigation, AccountSelection, BeforeScenario, WaitingRequest, WaitingCompletion, SessionChecks, Logout, PostLogout, FinalChecks, CleanupFreeze, CleanupThreads, CleanupUi, CleanupIdle }
    private volatile Checkpoint checkpoint = Checkpoint.Constructing;
    private int reportedForbidden;
    internal const string ProfileId = "11111111111111111111111111111111";
    internal const string Profile = "{\"id\":\"11111111111111111111111111111111\",\"name\":\"FixtureBUpdated\",\"skins\":[],\"capes\":[],\"fixture_future\":{\"preserve\":true}}";
    private readonly Stack<Action> restore = new();
    private readonly FixtureEnvironment environment;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentDictionary<Thread, byte> threads = new();
    private readonly ConcurrentQueue<Exception> producerErrors = new();
    private readonly List<DispatcherOperation> posted = new();
    private readonly object postGate = new();
    private readonly object startGate = new();
    private readonly AccountHost accounts;
    private RejectingConfigProvider? configGuard;
    private object? configLazy;
    private readonly ManualResetEventSlim entered = new(false), release = new(false);
    private readonly HashSet<string> allowed;
    private int forbidden, requestCount, refreshes;
    private Scenario scenario;
    private bool startsClosed;
    private bool disposed;
    private readonly object hintBuyGate = new();
    private string hintBuyPath = null!;
    private int hintBuyReads, hintBuyWrites;
    internal readonly PageLaunchLeft Launch;
    internal readonly MemoryStore Store;
    internal readonly List<(string Text, LegacyLoginHint Kind)> Hints = new();
    internal ModLoader.LoaderTask<ModLaunch.McLoginMs, ModLaunch.McLoginResult> Loader => ModLaunch.McLoginMsLoader;
    internal int RequestCount => Volatile.Read(ref requestCount);
    internal int Refreshes => Volatile.Read(ref refreshes);
    internal readonly int SeedTransactionCount;

    internal AccountUiChecksScope(FixtureEnvironment environment)
    {
        this.environment = environment;
        VisualAssetChecks.VerifyChild(environment);
        string[] args = Environment.GetCommandLineArgs(); int selected = Array.IndexOf(args, "--groups");
        Native.Check(selected >= 0 && selected + 1 < args.Length && args[selected + 1].Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).SequenceEqual(new[] { "account-ui" }), "Select account-ui alone in its fresh isolated child; captured Shared providers cannot be combined across groups.");
        Native.Check(!ModAnimation.AniRunning, "Account manual ticks cannot adopt a live animation driver.");
        // A rejected pre-existing producer is never ours to cancel in catch.
        Native.Check(Loader.State != ModBase.LoadState.Loading && Loader.LastRunningThread is not { IsAlive: true }, "Cannot adopt an existing Microsoft login producer.");
        Store = new MemoryStore(this);
        allowed = new HashSet<string>(new[] { "LoginType", "LoginMsJson", "CacheMsV2OAuthRefresh", "CacheMsV2Access", "CacheMsV2ProfileJson", "CacheMsV2Uuid", "CacheMsV2Name", "CacheMsV2Expires", "CacheMsV2Migrated" }, StringComparer.Ordinal);
        try
        {
            // Even a refused already-captured Shared provider enters cleanup.
            // Own the animation queue and clock before any cleanup can tick it.
            Dictionary<string, ModAnimation.AniGroupEntry> groups = new(ModAnimation.AniGroups);
            restore.Push(() => { ModAnimation.AniGroups.Clear(); foreach (var pair in groups) ModAnimation.AniGroups.Add(pair.Key, pair.Value); });
            ModAnimation.AniGroups.Clear();
            SaveStatic(typeof(ModAnimation), "AniLastTick");
            InstallSharedConfigGuard();
            accounts = (AccountHost)Native.Field(Native.Type("PCL.LoginPageRuntime"), "Accounts").GetValue(null)!;
            int suppression = ModAnimation.AniControlEnabled;
            restore.Push(() => ModAnimation.AniControlEnabled = suppression);
            ModAnimation.AniControlEnabled = 0;
            foreach (DictionaryEntry pair in Native.Entries)
            {
                IDictionary cache = (IDictionary)Native.Get(pair.Value!, "ValueCache")!;
                List<DictionaryEntry> before = new(); foreach (DictionaryEntry value in cache) before.Add(value);
                restore.Push(() => { cache.Clear(); foreach (var value in before) cache[value.Key] = value.Value; });
            }
            foreach (string name in new[] { "FrmMain", "FrmLaunchLeft", "FrmLoginLegacy", "FrmLoginMs", "FrmLoginMsSkin", "FrmLoginAuth", "FrmLoginAuthSkin", "FrmLoginNide", "FrmLoginNideSkin" }) Global(name, null);
            SaveStatic(typeof(ModMinecraft), "_McInstanceSelected"); Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, null);
            // Actual loader thread/cancellation fields are preserved, not replaced
            // by a task-state stub. All started bodies are joined before restore.
            // Restore backing fields only after producer/UI barriers. Invoking
            // State/Progress setters during restoration would post new callbacks.
            for (Type? type = Loader.GetType(); type is not null; type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(Native.Flags | BindingFlags.DeclaredOnly).Where(field => !field.IsStatic && !field.IsInitOnly))
                { object? before = field.GetValue(Loader); restore.Push(() => field.SetValue(Loader, before)); }
            // Register the actual native body before its first production
            // instruction. A fast pre-HTTP failure may clear LastRunningThread.
            var load = (Action<ModLoader.LoaderTask<ModLaunch.McLoginMs, ModLaunch.McLoginResult>>)Native.Get(Loader, "LoadDelegate")!;
            Replace(Loader, "LoadDelegate", new Action<ModLoader.LoaderTask<ModLaunch.McLoginMs, ModLaunch.McLoginResult>>(actual => { threads.TryAdd(Thread.CurrentThread, 0); load(actual); }));
            Action<object?, bool> start = accounts.MsLogin.Start;
            Replace(accounts.MsLogin, "Start", new Action<object?, bool>((input, force) => {
                lock (startGate) {
                    if (startsClosed) throw new OperationCanceledException("Owned account starts are closed.");
                    try { start(input, force); }
                    finally { ObserveLoaderThread(); } // Retain before cleanup can Cancel/clear it.
                }
            }));
            Action cancel = accounts.MsLogin.Cancel;
            Replace(accounts.MsLogin, "Cancel", new Action(() => { lock (startGate) { ObserveLoaderThread(); cancel(); } }));
            Replace(accounts, "RunInUi", new Action<Action>(Dispatch));
            Replace(accounts, "RunInUiWait", new Action<Action>(DispatchWait));
            Replace(accounts, "RunInThread", new Action<Action>(work => Spawn(work, "Account UI platform")));
            Replace(accounts, "RunInNewThread", new Action<Action, string>(Spawn));
            Func<string, string> readAccount = accounts.ReadString;
            Replace(accounts, "ReadString", new Func<string, string>(key => key is "LoginMsJson" or "CacheMsV2OAuthRefresh" or "CacheMsV2Name" ? readAccount(key) : Forbidden<string>("unexpected account string read", key)));
            Replace(accounts, "ReadInstanceString", new Func<string, string>(key => Forbidden<string>("unexpected account instance read", key)));
            Replace(accounts, "ReadBoolean", new Func<string, bool>(key => Forbidden<bool>("unexpected account boolean read", key)));
            Replace(accounts, "ReadInteger", new Func<string, int>(key => Forbidden<int>("unexpected account integer read", key)));
            Func<string, Dictionary<string, object?>> accountSnapshot = accounts.AccountSnapshot;
            Replace(accounts, "AccountSnapshot", new Func<string, Dictionary<string, object?>>(prefix => prefix == "CacheMsV2" ? accountSnapshot(prefix) : Forbidden<Dictionary<string, object?>>("unexpected account snapshot")));
            Replace(accounts, "Hint", new Action<string, LegacyLoginHint>((text, kind) => Dispatch(() => Hints.Add((text, kind)))));
            Replace(accounts, "OpenWebsite", new Action<string>(_ => Forbidden("unexpected website")));
            Replace(accounts, "RequestRetry", new Func<string, System.Net.Http.HttpMethod, object?, string?, Dictionary<string, string>?, string>((_, _, _, _, _) => Forbidden<string>("unexpected skin/cape/upload HTTP")));
            Action<bool, bool> refresh = accounts.RefreshLaunchPage;
            Replace(accounts, "RefreshLaunchPage", new Action<bool, bool>((keep, animate) => { Interlocked.Increment(ref refreshes); refresh(keep, animate); }));
            object loaderHost = Native.Field(typeof(ModLoader), "NativeLoaderHost").GetValue(null)!;
            Replace(loaderHost, "RunInUi", new Action<Action>(Dispatch));
            Replace(loaderHost, "RunInUiWait", new Action<Action>(DispatchWait));
            Replace(loaderHost, "RunInThread", new Action<Action>(work => Spawn(work, "Account loader callback")));
            object service = Native.Field(typeof(ModLaunch), "Service").GetValue(null)!;
            object host = service.GetType().GetFields(Native.Flags).Single(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(LegacyLaunchHost<,,,,,,>)).GetValue(service)!;
            var snapshot = (Func<string, Dictionary<string, object?>>)Native.Get(host, "AccountSnapshot")!;
            Replace(host, "AccountSnapshot", new Func<string, Dictionary<string, object?>>(prefix => prefix == "CacheMsV2" ? snapshot(prefix) : Forbidden<Dictionary<string, object?>>("unexpected loader account snapshot")));
            Replace(host, "ReadString", new Func<string, ModMinecraft.McInstance?, string>((key, _) => Forbidden<string>("unexpected loader string setting read", key)));
            Replace(host, "ReadInteger", new Func<string, ModMinecraft.McInstance?, int>((key, _) => Forbidden<int>("unexpected loader integer setting read", key)));
            Replace(host, "ReadBoolean", new Func<string, ModMinecraft.McInstance?, bool>((key, _) => Forbidden<bool>("unexpected loader boolean setting read", key)));
            Replace(host, "Request", new Func<LegacyLaunchRequest, string>(Request));
            Replace(host, "OAuthClientId", new Func<string>(() => "native-account-fixture-client"));
            Replace(host, "DeviceLoginPrompt", new Func<JObject, Func<LegacyLaunchDeviceResult?>>(_ => Forbidden<Func<LegacyLaunchDeviceResult?>>("unexpected device authorization")));
            Replace(host, "OpenWebsite", new Action<string>(_ => Forbidden("unexpected OAuth website")));
            Replace(host, "Message", new Func<LegacyLaunchMessage, int>(_ => Forbidden<int>("unexpected OAuth modal")));
            Replace(host, "AppendLog", new Action<string>(_ => { })); // Logger remains real; no absent launch-log UI is invented.
            Replace(host, "Dispatch", new Action<Action>(Dispatch));
            Replace(host, "DispatchWait", new Action<Action>(DispatchWait));
            Replace(host, "NewThread", new Action<Action, string>(Spawn));
            object network = Native.Field(typeof(ModNet), "NativeHost").GetValue(null)!;
            Replace(network, "TempFolder", new Func<string>(() => Forbidden<string>("unexpected raw HTTP/cache IO")));
            Replace(network, "RequestTempFolder", new Func<string>(() => Forbidden<string>("unexpected download IO")));
            Replace(network, "RunNewThread", new Func<Action, string, ThreadPriority, Thread>((_, _, _) => Forbidden<Thread>("unexpected network worker")));
            InstallSettingsPlatform();
            // Account GetCore deliberately bypasses ValueCache. Seed via the
            // genuine encrypted transaction, then separate it from the session
            // transaction whose order/content the consumer checks below.
            Settings.SaveAccountGroup(new Dictionary<string, object> {
                ["LoginMsJson"] = "{\"FixtureA\":\"fixture-refresh-a\",\"FixtureB\":\"fixture-refresh-b\"}",
                ["CacheMsV2Migrated"] = true, ["CacheMsV2Expires"] = 0L,
                ["CacheMsV2OAuthRefresh"] = "", ["CacheMsV2Access"] = "", ["CacheMsV2ProfileJson"] = "", ["CacheMsV2Uuid"] = "", ["CacheMsV2Name"] = ""
            });
            SeedTransactionCount = Store.Transactions.Count;
            Native.Check(SeedTransactionCount == 1 && Store.Transactions[0].Length == 8 && Store.Transactions[0][^1] == "CacheMsV2Migrated_LF", "The fixture seed did not use the original encrypted marker-last account transaction.");
            ModBase.RunInUi(); // Initialize Main's real UI-thread identity.
            Launch = new PageLaunchLeft();
            Native.Field(typeof(ModMain), "FrmLaunchLeft").SetValue(null, Launch);
            object panelHost = Native.Get(Native.Get(Launch, "Native")!, "host")!;
            var panelString = (Func<string, string>)Native.Get(panelHost, "ReadString")!;
            var panelInteger = (Func<string, int>)Native.Get(panelHost, "ReadInteger")!;
            Replace(panelHost, "ReadString", new Func<string, string>(key => key == "CacheMsV2Access" ? panelString(key) : Forbidden<string>("unexpected launch-panel string setting read", key)));
            Replace(panelHost, "ReadInteger", new Func<string, int>(key => key == "LoginPageType" ? panelInteger(key) : Forbidden<int>("unexpected launch-panel integer setting read", key)));
            Replace(panelHost, "ReadBoolean", new Func<string, bool>(key => Forbidden<bool>("unexpected launch-panel boolean setting read", key)));
            checkpoint = Checkpoint.Ready;

        }
        catch (Exception error)
        {
            ReportPrimary(error);
            try { StopAndDrain(); } catch (Exception cleanup) { VisualAssetChecks.FailClosed(environment, "Account constructor producers did not stop before restore.", new AggregateException(error, cleanup)); }
            Restore(); throw;
        }
    }

    private void InstallSharedConfigGuard()
    {
        // Default's concrete provider and ConfigEntry's captured readonly
        // providers are different boundaries. Never rewrite readonly fields.
        // This explicitly solo child installs Default before Configs initializes
        // and rejects any previously captured provider without reading it.
        MeloongCore.JsonConfigProvider before = MeloongCore.ConfigProviders.Default;
        string path = Path.Combine(environment.Data, "account-ui", "forbidden-shared.json");
        configGuard = new RejectingConfigProvider(this, path);
        configLazy = new Lazy<JObject>(() => Forbidden<JObject>("unexpected concrete Shared config access"));
        Native.Field(typeof(MeloongCore.JsonConfigProvider), "json").SetValue(configGuard, configLazy);
        restore.Push(() => { MeloongCore.ConfigProviders.Default = before; Native.Check(ReferenceEquals(MeloongCore.ConfigProviders.Default, before), "Shared Default guard did not restore original identity."); });
        MeloongCore.ConfigProviders.Default = configGuard;
        RuntimeHelpers.RunClassConstructor(typeof(PCLCS.Configs).TypeHandle);
        var entries = typeof(PCLCS.Configs).GetFields(Native.Flags).Where(field => field.IsStatic && field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(MeloongCore.ConfigEntry<>)).ToArray();
        Native.Check(entries.Length > 0 && entries.All(field => ReferenceEquals(field.FieldType.GetProperty("Provider")!.GetValue(field.GetValue(null)), configGuard)), "A Shared ConfigEntry captured a pre-existing provider; refuse to adopt or read it.");
    }
    private void AssertSharedGuard()
    {
        if (configGuard is null) return;
        Native.Check(ReferenceEquals(MeloongCore.ConfigProviders.Default, configGuard) && ReferenceEquals(Native.Field(typeof(MeloongCore.JsonConfigProvider), "json").GetValue(configGuard), configLazy) && !(bool)Native.Field(typeof(MeloongCore.JsonConfigProvider), "isDirty").GetValue(configGuard)!, "Shared concrete config provider was replaced/reset/mutated unexpectedly.");
    }

    private void InstallSettingsPlatform()
    {
        object backend = Native.Field(typeof(Settings), "Backend").GetValue(null)!;
        object host = Native.Get(backend, "host")!;
        // LF f4/main50 and fixed PCL5ef define HintBuy as an unencrypted
        // Boolean Registry setting, not INI and not a Microsoft account key.
        Settings.Setting hint = Settings.Entries["HintBuy"];
        Native.Check(hint.Key == "HintBuy" && hint.Source == Settings.Sources.Registry && !hint.Encrypted && hint.Type == typeof(bool) && hint.DefaultValue is false,
            "Actual HintBuy schema changed; refuse to widen the account platform fixture.");
        string hintFolder = (string)Native.Static("PCL.Settings", "RegistryFolderFor", hint)!;
        string hintKey = (string)Native.Static("PCL.Settings", "RegistryValueName", hint)!;
        Native.Check(hintFolder == "PCLDebug" && hintKey == "HintBuy" && Equals(Native.Field(Native.Type("PCL.ModSecret"), "RegFolder").GetRawConstantValue(), hintFolder),
            "Actual HintBuy Registry namespace/key changed; no account-key alias is allowed.");
        string hintRegistry = @"HKEY_CURRENT_USER\Software\" + hintFolder;
        hintBuyPath = Path.Combine(environment.Data, "account-ui", "HintBuy.REG_SZ");
        Native.Check(Native.Full(hintBuyPath).StartsWith(Native.Full(environment.Data) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !File.Exists(hintBuyPath), "Refusing a pre-existing or unowned HintBuy platform leaf.");
        Directory.CreateDirectory(Path.GetDirectoryName(hintBuyPath)!);
        Replace(host, "ReadRegistry", new Func<string, string, string?, string?>((folder, key, fallback) => {
            if (folder == hintRegistry && key == hintKey) { lock (hintBuyGate) { Interlocked.Increment(ref hintBuyReads); return File.Exists(hintBuyPath) ? File.ReadAllText(hintBuyPath, System.Text.Encoding.UTF8) : fallback; } }
            CheckRegistry(folder, key); return Store.GetValue(key, fallback) as string;
        }));
        Replace(host, "WriteRegistry", new Action<string, string, string?>((folder, key, value) => {
            if (folder == hintRegistry && key == hintKey) {
                Native.Check(value is not null, "Original HintBuy REG_SZ must contain a string.");
                lock (hintBuyGate) { File.WriteAllText(hintBuyPath, value!, new System.Text.UTF8Encoding(false)); Interlocked.Increment(ref hintBuyWrites); } return;
            }
            CheckRegistry(folder, key); Store.SetValue(key, value!, RegistryValueKind.String);
        }));
        Replace(host, "HasRegistry", new Func<string, string, bool>((folder, key) => {
            if (folder == hintRegistry && key == hintKey) { lock (hintBuyGate) return File.Exists(hintBuyPath); }
            CheckRegistry(folder, key); return Store.GetValue(key) is not null;
        }));
        Replace(host, "OpenRegistry", new Func<string, bool, bool, ILegacySettingsStore?>((folder, write, create) => { if (folder != "PCL" || !write || !create) return Forbidden<ILegacySettingsStore?>("unexpected account-store open"); return Store.Open(); }));
        Replace(host, "ReadIni", new Func<string, string, string?, string?>((_, key, _) => Forbidden<string?>("unexpected account INI read", key)));
        Replace(host, "WriteIni", new Action<string, string, string?>((_, key, _) => Forbidden("unexpected account INI write", key)));
        hint.Save(); // The genuine Setting.Save/SaveEntry creates the seed REG_SZ.
        Native.Check(Volatile.Read(ref hintBuyWrites) == 1 && File.Exists(hintBuyPath) && File.ReadAllText(hintBuyPath, System.Text.Encoding.UTF8) == "False", "Original HintBuy Save did not persist its false Boolean seed.");
    }
    internal void AssertPurchaseHintSaved()
    {
        Native.Check(Volatile.Read(ref hintBuyWrites) == 2, "Original successful login must write HintBuy once; rejected/canceled login must not rewrite it.");
        int reads = Volatile.Read(ref hintBuyReads);
        Settings.Entries["HintBuy"].ValueCache.TryRemove("", out _);
        object value = Settings.Get("HintBuy");
        Native.Check(value is bool saved && saved && Settings.Get<bool>("HintBuy") && Volatile.Read(ref hintBuyReads) == reads + 1,
            "Original Settings cold Registry read did not round-trip the saved HintBuy Boolean.");
    }
    private void CheckRegistry(string folder, string key)
    {
        if (folder != (key == "LoginType" ? @"HKEY_CURRENT_USER\Software\PCL" : @"HKEY_CURRENT_USER\Software\PCL Living Fork")) Forbidden("unexpected account Registry folder");
        CheckKey(key);
    }
    internal void CheckKey(string physical)
    {
        string logical = physical.EndsWith("_LF", StringComparison.Ordinal) ? physical[..^3] : physical;
        if (!allowed.Contains(logical) || physical != (logical == "LoginType" ? logical : logical + "_LF")) Forbidden("unexpected account physical key", logical);
    }
    private T Forbidden<T>(string operation, string? key = null) {
        Interlocked.Increment(ref forbidden);
        if (Interlocked.CompareExchange(ref reportedForbidden, 1, 0) == 0) {
            try { TryReport("first-forbidden", new { Operation = operation, SchemaKey = SafeKey(key), Stack = SafeStack(new StackTrace(1, true)) }); }
            catch { } // Even diagnostic-payload construction cannot replace the guard.
        }
        throw new InvalidOperationException("Forbidden account fixture boundary: " + operation);
    }
    private void Forbidden(string operation, string? key = null) => Forbidden<int>(operation, key);
    private static string SafeKey(string? key) {
        if (key is null) return "<none>";
        string logical = key.EndsWith("_LF", StringComparison.Ordinal) ? key[..^3] : key;
        return Native.Entries.Contains(logical) || logical is "MinecraftInstallManifest" or "JavaConfigVersion" or "JavaList" or "JavaRemovedList" or "InstanceMigratedJava" or "InstanceForcedJava" or "HintModifyInstanceWarning" ? logical : "<unknown-schema-key>";
    }
    private static string[] SafeStack(StackTrace trace) => (trace.GetFrames() ?? []).Take(18).Select(frame => {
        MethodBase? method = frame.GetMethod();
        Type? owner = method?.DeclaringType;
        if (owner?.IsGenericType == true) owner = owner.GetGenericTypeDefinition();
        string name = (owner?.FullName ?? "<unknown-type>") + "." + (method?.Name ?? "<unknown-method>");
        int line = frame.GetFileLineNumber(); // Never disclose GetFileName/path or arguments.
        if (line > 0) name += ":line " + line;
        return name.Length <= 400 ? name : name[..400];
    }).ToArray();
    private static object SafeError(Exception error) => new { Type = error.GetType().FullName, Stack = SafeStack(new StackTrace(error, true)), InnerTypes = InnerTypes(error) };
    private static string?[] InnerTypes(Exception error) {
        List<string?> types = new();
        for (Exception? inner = error.InnerException; inner is not null && types.Count < 4; inner = inner.InnerException) types.Add(inner.GetType().FullName);
        return types.ToArray();
    }
    internal void At(Checkpoint value) => checkpoint = value;
    internal void ReportPrimary(Exception error) => TryReportError("body-primary", error);
    private void TryReportError(string kind, Exception error) { try { TryReport(kind, SafeError(error)); } catch { } }
    private void TryReport(string kind, object detail) {
        try {
            var posts = new Dictionary<string, int>(StringComparer.Ordinal);
            lock (postGate) foreach (var operation in posted) { string status = operation.Status.ToString(); posts[status] = posts.GetValueOrDefault(status) + 1; }
            var owned = threads.Keys.Select(thread => new {
                Id = thread.ManagedThreadId,
                Name = thread.Name is "Ms Login" or "Account UI platform" or "Account loader callback" or "L/Loader Login Ms" or "Login Failed: Create Profile" ? thread.Name : "<unrecognized-owned-name>",
                Alive = thread.IsAlive, State = thread.ThreadState.ToString()
            }).ToArray();
            Console.Error.WriteLine("ACCOUNT SAFE DIAGNOSTIC " + System.Text.Json.JsonSerializer.Serialize(new {
                Kind = kind, Stage = checkpoint.ToString(), Scenario = scenario.ToString(),
                ForbiddenCount = Volatile.Read(ref forbidden), ProducerErrorCount = producerErrors.Count,
                LoaderState = Loader.State.ToString(), LoaderError = Loader.Error is Exception error ? SafeError(error) : null, OwnedThreads = owned, Posted = posts,
                ProducerErrors = producerErrors.Take(3).Select(SafeError).ToArray(), Detail = detail
            }));
        } catch { /* Diagnostics never replace the original exception/guard. */ }
    }
    private void Replace(object owner, string name, object value) { FieldInfo field = Native.Field(owner.GetType(), name); object? before = field.GetValue(owner); restore.Push(() => { field.SetValue(owner, before); Native.Check(ReferenceEquals(field.GetValue(owner), before), "Account seam identity restoration failed: " + name); }); field.SetValue(owner, value); }
    private void SaveField(object owner, string name) { FieldInfo field = Native.Field(owner.GetType(), name); object? before = field.GetValue(owner); restore.Push(() => field.SetValue(owner, before)); }
    private void SaveStatic(Type type, string name) { FieldInfo field = Native.Field(type, name); object? before = field.GetValue(null); restore.Push(() => field.SetValue(null, before)); }
    private void Global(string name, object? value) { SaveStatic(typeof(ModMain), name); Native.Field(typeof(ModMain), name).SetValue(null, value); }
    internal void SetCache(string name, object value) => Native.Cache(name, value);
    internal void VerifyController(PageLoginMs page) => Native.Check(ReferenceEquals(Native.Get(Native.Get(page, "Native")!, "host"), accounts), "Actual Microsoft page must close over the original LoginPageRuntime host.");

    private void Dispatch(Action work)
    {
        if (dispatcher.CheckAccess()) { work(); return; }
        DispatcherOperation operation = dispatcher.BeginInvoke(DispatcherPriority.Normal, work);
        lock (postGate) posted.Add(operation);
    }
    private void DispatchWait(Action work) { if (dispatcher.CheckAccess()) work(); else dispatcher.Invoke(work); }
    private void Spawn(Action work, string name)
    {
        lock (startGate) {
            Thread thread = new(() => { try { work(); } catch (Exception error) { producerErrors.Enqueue(error); TryReportError("producer-error", error); } }) { Name = name };
            threads.TryAdd(thread, 0); thread.Start();
        }
    }
    private void ObserveLoaderThread() { if (Loader.LastRunningThread is Thread thread) threads.TryAdd(thread, 0); }
    internal bool IsCurrent<T>() where T : FrameworkElement => AccountUiChecks.Part<Panel>(Launch, "PanLogin").Children.Count == 1 && AccountUiChecks.Part<Panel>(Launch, "PanLogin").Children[0] is T;
    internal T Current<T>() where T : FrameworkElement { Native.Check(IsCurrent<T>(), "Actual launch navigation is not " + typeof(T).Name); return (T)AccountUiChecks.Part<Panel>(Launch, "PanLogin").Children[0]; }
    internal void BeginScenario(Scenario next)
    {
        checkpoint = Checkpoint.BeforeScenario;
        AssertIdle(); entered.Reset(); release.Reset(); Volatile.Write(ref requestCount, 0); scenario = next;
    }
    internal void ReleaseRequest() => release.Set();
    internal void AwaitRequest() { checkpoint = Checkpoint.WaitingRequest; AwaitUi(() => entered.IsSet); }
    internal void AwaitUi(Func<bool> done)
    {
        VisualAssetChecks.Pump(() => { ObserveLoaderThread(); AdvanceAnimations(); return done(); }, "actual account UI/producer checkpoint");
    }
    internal void AwaitCompletion(ModBase.LoadState expected, Func<bool> ui)
    {
        checkpoint = Checkpoint.WaitingCompletion;
        AwaitUi(() => Loader.State == expected && threads.Keys.All(thread => !thread.IsAlive) && ui());
        foreach (Thread thread in threads.Keys) Native.Check(thread.Join(0), "Actual account producer has not joined.");
        VisualAssetChecks.Drain();
        Native.Check(Loader.LoadingState == (expected == ModBase.LoadState.Failed ? MyLoading.MyLoadingState.Error : MyLoading.MyLoadingState.Stop), "Actual loader UI completion state did not drain.");
        AssertIdle();
    }
    private static void AdvanceAnimations()
    {
        Native.Check(!ModAnimation.AniRunning, "A real animation driver was started by the account fixture.");
        Native.Field(typeof(ModAnimation), "AniLastTick").SetValue(null, ModBase.GetTimeMs());
        ModAnimation.AniTimer(50);
    }
    internal void AssertIdle()
    {
        Native.Check(Monitor.TryEnter(startGate), "An actual account producer still owns a pre-start reservation.");
        try {
            ObserveLoaderThread();
            bool idle = Volatile.Read(ref forbidden) == 0 && producerErrors.IsEmpty && Loader.State != ModBase.LoadState.Loading && threads.Keys.All(thread => !thread.IsAlive && thread.Join(0));
            if (!idle) TryReport("idle-rejected", new { ForbiddenClear = Volatile.Read(ref forbidden) == 0, ProducerErrorsClear = producerErrors.IsEmpty, LoaderTerminal = Loader.State != ModBase.LoadState.Loading });
            Native.Check(idle, "Account scope retained forbidden IO, an uncaught producer error, or a real login/thread producer.");
            lock (postGate) {
                bool completed = posted.All(operation => operation.Status == DispatcherOperationStatus.Completed);
                if (!completed) TryReport("posted-rejected", new { Completed = false });
                Native.Check(completed, "Account scope retained an actual queued UI callback.");
            }
        } finally { Monitor.Exit(startGate); }
        environment.AssertNoForbiddenIo();
        AssertSharedGuard();
    }
    private string Request(LegacyLaunchRequest request)
    {
        threads.TryAdd(Thread.CurrentThread, 0);
        int step = Interlocked.Increment(ref requestCount) - 1;
        string[] urls = ["https://login.live.com/oauth20_token.srf", "https://user.auth.xboxlive.com/user/authenticate", "https://xsts.auth.xboxlive.com/xsts/authorize", "https://api.minecraftservices.com/authentication/login_with_xbox", "https://api.minecraftservices.com/entitlements/mcstore", "https://api.minecraftservices.com/minecraft/profile"];
        if (scenario == Scenario.None || step >= urls.Length || request.Url != urls[step] || request.Kind != (step == 3 ? LegacyLaunchRequestKind.Retry : LegacyLaunchRequestKind.Multiple) || (step < 4 && request.Method != System.Net.Http.HttpMethod.Post) || (step >= 4 && request.Method is not null)) return Forbidden<string>("unexpected OAuth HTTP request/order");
        if (request.ContentType != (step == 0 ? "application/x-www-form-urlencoded" : "application/json") || request.RequireJson != (step != 0) || request.ThreadCount != (step is 0 or 4 or 5 ? 2 : null)) return Forbidden<string>("unexpected OAuth content/JSON/thread-count contract");
        if (step is 1 or 2 or 3 && request.Headers is not null) return Forbidden<string>("unexpected intermediate OAuth headers");
        if (step == 0 && (request.Headers is null || request.Headers.GetLength(0) != 2 || request.Headers[0, 0] != "Accept-Language" || request.Headers[0, 1] != "en-US,en;q=0.5" || request.Headers[1, 0] != "X-Requested-With" || request.Headers[1, 1] != "XMLHttpRequest")) return Forbidden<string>("unexpected OAuth refresh headers");
        string content = request.Content?.ToString() ?? "";
        if (step == 0)
        {
            if (!content.Contains("client_id=native-account-fixture-client&refresh_token=fixture-refresh-", StringComparison.Ordinal) || !content.EndsWith("&grant_type=refresh_token&scope=XboxLive.signin%20offline_access", StringComparison.Ordinal)) return Forbidden<string>("unexpected OAuth refresh input");
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("Actual account request gate was not released/canceled.");
            if (scenario == Scenario.Failure) throw new Exception("$native account fixture denied");
            if (scenario == Scenario.Cancel) throw new OperationCanceledException();
        }
        if (step == 1 && (string?)JObject.Parse(content)["Properties"]?["RpsTicket"] != "d=fixture-oauth-access") return Forbidden<string>("unexpected Xbox refresh token propagation");
        if (step == 2 && (string?)JObject.Parse(content)["Properties"]?["UserTokens"]?[0] != "fixture-xbl") return Forbidden<string>("unexpected XSTS token propagation");
        if (step == 3 && (string?)JObject.Parse(content)["identityToken"] != "XBL3.0 x=fixture-uhs;fixture-xsts") return Forbidden<string>("unexpected Minecraft identity propagation");
        if (step >= 4 && (request.Headers is null || request.Headers.GetLength(0) != 1 || request.Headers[0, 0] != "Authorization" || request.Headers[0, 1] != "Bearer fixture-minecraft-access")) return Forbidden<string>("unexpected Minecraft bearer propagation");
        return step switch
        {
            0 => "{\"access_token\":\"fixture-oauth-access\",\"refresh_token\":\"fixture-refresh-new\"}",
            1 => "{\"Token\":\"fixture-xbl\"}",
            2 => "{\"Token\":\"fixture-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"fixture-uhs\"}]}}",
            3 => "{\"access_token\":\"fixture-minecraft-access\",\"expires_in\":86400}",
            4 => "{\"items\":[{\"name\":\"synthetic-entitlement-only\"}]}",
            5 => Profile,
            _ => Forbidden<string>("unexpected OAuth response step")
        };
    }
    private void StopAndDrain()
    {
        // Pump while an existing Start owns the gate rather than block the STA
        // behind a producer which may need its original UI input callback.
        // Once this barrier wins, no later TaskPort.Start can create a native
        // thread; an already assigned thread is retained before Cancel clears it.
        checkpoint = Checkpoint.CleanupFreeze;
        AwaitUi(() => {
            if (!Monitor.TryEnter(startGate)) return false;
            try { startsClosed = true; ObserveLoaderThread(); Loader.Cancel(); release.Set(); return true; }
            finally { Monitor.Exit(startGate); }
        });
        checkpoint = Checkpoint.CleanupThreads;
        AwaitUi(() => threads.Keys.All(thread => !thread.IsAlive));
        foreach (Thread thread in threads.Keys) Native.Check(thread.Join(0), "Account cleanup did not join a real producer.");
        VisualAssetChecks.Drain();
        checkpoint = Checkpoint.CleanupUi;
        for (int i = 0; i < 40 && ModAnimation.AniGroups.Count != 0; i++) AdvanceAnimations();
        VisualAssetChecks.Drain();
        checkpoint = Checkpoint.CleanupIdle;
        AssertIdle();
        Native.Check(ModAnimation.AniGroups.Count == 0, "Account animation callbacks did not finish before restoration.");
        Native.Check(!Launch?.IsLoaded ?? true, "Detached account fixture unexpectedly loaded its entire launch page.");
        Native.Check(PageLaunchLeft.SkinLoaders.All(loader => loader.State != ModBase.LoadState.Loading && loader.LastRunningThread is not { IsAlive: true }), "Account scope started an uncontrolled skin loader.");
        if (configGuard is not null) {
            Native.Check(Monitor.TryEnter(configGuard), "An owned Shared config save still holds its real provider monitor.");
            try { AssertSharedGuard(); ((IDisposable)Native.Field(typeof(MeloongCore.JsonConfigProvider), "saveWorker").GetValue(configGuard)!).Dispose(); }
            finally { Monitor.Exit(configGuard); }
        }
    }
    private void Restore() { while (restore.TryPop(out Action? action)) action(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { StopAndDrain(); } catch (Exception error) { VisualAssetChecks.FailClosed(environment, "Actual account producers/UI did not stop before guard restoration.", error); }
        Restore(); entered.Dispose(); release.Dispose();
    }

    internal sealed class MemoryStore(AccountUiChecksScope owner) : ILegacySettingsStore
    {
        private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> values = new(StringComparer.Ordinal);
        internal readonly List<string> Writes = new();
        internal readonly List<string[]> Transactions = new();
        private int transactionStart = -1;
        internal MemoryStore Open() { if (transactionStart >= 0) owner.Forbidden("unexpected overlapping account transaction"); transactionStart = Writes.Count; return this; }
        public object? GetValue(string name, object? defaultValue = null, bool doNotExpandEnvironmentNames = false) { owner.CheckKey(name); return values.TryGetValue(name, out var value) ? value.Value : defaultValue; }
        public RegistryValueKind GetValueKind(string name) { owner.CheckKey(name); return values[name].Kind; }
        public void SetValue(string name, object value, RegistryValueKind kind) { owner.CheckKey(name); if (kind != RegistryValueKind.String || value is not string) owner.Forbidden("unexpected account storage type"); values[name] = (value, kind); Writes.Add(name); }
        public void DeleteValue(string name, bool throwOnMissingValue) { owner.CheckKey(name); if (!values.Remove(name) && throwOnMissingValue) throw new ArgumentException("Missing owned value"); }
        public void Dispose() { if (transactionStart < 0) owner.Forbidden("unexpected account store dispose"); Transactions.Add(Writes.Skip(transactionStart).ToArray()); transactionStart = -1; }
        internal Dictionary<string, (object Value, RegistryValueKind Kind)> Snapshot() => new(values, StringComparer.Ordinal);
        internal bool EqualsSnapshot(Dictionary<string, (object Value, RegistryValueKind Kind)> before) => values.Count == before.Count && before.All(pair => values.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value));
    }

    private sealed class RejectingConfigProvider(AccountUiChecksScope owner, string path) : MeloongCore.JsonConfigProvider(path), MeloongCore.IConfigProvider
    {
        void MeloongCore.IConfigProvider.Set<T>(string key, T? value, bool encrypted) where T : default => owner.Forbidden("unexpected Shared config write", key);
        void MeloongCore.IConfigProvider.Remove(string key) => owner.Forbidden("unexpected Shared config removal", key);
        T? MeloongCore.IConfigProvider.Read<T>(string key, T? defaultValue, bool encrypted) where T : default => owner.Forbidden<T?>("unexpected Shared config read", key);
        bool MeloongCore.IConfigProvider.HasValue(string key) => owner.Forbidden<bool>("unexpected Shared config probe", key);
        void MeloongCore.IConfigProvider.DiscardCache() => owner.Forbidden("unexpected Shared config reset");
    }
}
