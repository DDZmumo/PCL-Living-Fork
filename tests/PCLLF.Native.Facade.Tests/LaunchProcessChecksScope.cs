using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using LaunchCore = PCLCS.LegacyLaunchEngine<PCL.ModMinecraft.McInstance, PCL.ModMinecraft.McInstance.McInstanceState, PCL.ModMinecraft.McInstanceCardType, PCL.ModLaunch.McLaunchOptions, PCL.ModLaunch.McLoginData, PCL.ModLaunch.McLoginServer, PCL.ModLaunch.McLoginMs, PCL.ModLaunch.McLoginLegacy, PCL.ModWatcher.Watcher>;
using LaunchHost = PCLCS.LegacyLaunchHost<PCL.ModMinecraft.McInstance, PCL.ModLaunch.McLaunchOptions, PCL.ModLaunch.McLoginData, PCL.ModLaunch.McLoginServer, PCL.ModLaunch.McLoginMs, PCL.ModLaunch.McLoginLegacy, PCL.ModWatcher.Watcher>;
using WatcherHost = PCLCS.LegacyWatcherHost<PCL.ModWatcher.Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<System.Diagnostics.Process, int>, PCL.ModWatcher.Watcher.MinecraftState>;

namespace PCLLF.Native.Facade.Tests;

internal sealed partial class LaunchProcessChecksScope : IDisposable
{
    private readonly FileTaskScope tasks;
    private readonly FixtureEnvironment environment;
    private readonly Stack<Action> restore = new();
    private readonly LaunchHost launch;
    private readonly WatcherHost watcher;
    private readonly LaunchCore service;
    private readonly ConcurrentDictionary<Thread, byte> threads = new();
    private readonly ConcurrentDictionary<Process, byte> owned = new();
    private readonly ConcurrentDictionary<Process, int> outputClosed = new(), errorClosed = new();
    private readonly ConcurrentDictionary<Process, Thread> drainThreads = new();
    private readonly ConcurrentQueue<Exception> asyncErrors = new();
    private readonly string root, game, profile, options, command, cmd, fakeJava;
    private readonly int exitCode;
    private int pending, generation, forbidden, activeHandlers, startRequested, firstStartReservation;
    private readonly Thread uiThread = Thread.CurrentThread;
    private volatile bool schedulingFailed;
    private bool disposed;
    private Exception? primaryFailure;
    private readonly object launchCountGate = new();
    private string? launchCountStore;
    private int launchCountWrites, launchCountReads;
    private ModMinecraft.McInstance instance = null!;
    private ILegacyLaunchOptions<ModMinecraft.McInstance> prepared = null!;
    internal ModLoader.LoaderCombo<object> Plan = null!;
    internal bool CancelAfterStart;
    internal int Hidden, Restored, ProcessStopped, PersonalFinished, QuickFinished, HealthyCalls, ProcessStarts, CrashHints, Analyzers, AnalysisCompleted, AnalysisDialogs;
    internal bool LastHealthy;
    internal readonly int InitialLaunchCount;
    internal string OutputMarker { get; }
    internal string ErrorMarker { get; }
    internal Process Child => owned.Keys.Single();

    internal LaunchProcessChecksScope(FixtureEnvironment environment, string name, int exitCode)
    {
        this.environment = environment; this.exitCode = exitCode;
        tasks = new FileTaskScope(environment);
        try
        {
            root = Path.Combine(tasks.Root, "launch-process", name); game = Path.Combine(root, "minecraft"); profile = Path.Combine(root, "profile");
            Native.Check(!Directory.Exists(root), "Refusing an existing launch-process fixture."); Directory.CreateDirectory(root);
            cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Native.Check(Path.IsPathFullyQualified(cmd) && File.Exists(cmd) && Path.GetFileName(cmd) == "cmd.exe", "Absolute Windows system cmd.exe is required.");
            foreach (string owner in new[] { "PCL.LocalGameData", "PCL.WorldBackups", "PCL.EnvironmentHistory" })
            {
                PropertyInfo field = Native.Property(Native.Type(owner), "TestRoot"); object? before = field.GetValue(null);
                restore.Push(() => field.SetValue(null, before)); field.SetValue(null, owner == "PCL.LocalGameData" ? profile : Path.Combine(root, owner.Split('.').Last()));
            }
            foreach (string nameField in new[] { "CurrentLaunchOptions", "McLaunchLoaderReal", "McLaunchProcess", "McLaunchWatcher", "McLaunchJavaSelected", "McLaunchArgument", "McLaunchEncoding", "McLoginLoader" }) SaveStatic(typeof(ModLaunch), nameField);
            SaveStatic(typeof(ModWatcher), "McWatcherList"); SaveStatic(typeof(ModWatcher), "HasRunningMinecraft");
            object group = Native.Field(typeof(ModWatcher), "Group").GetValue(null)!; Set(group, "isWatcherRunning", false);
            ModWatcher.McWatcherList = new(); ModWatcher.HasRunningMinecraft = false;
            ModLaunch.McLaunchProcess = null!; ModLaunch.McLaunchWatcher = null!;
            service = (LaunchCore)Native.Field(typeof(ModLaunch), "Service").GetValue(null)!; launch = (LaunchHost)service.GetType().GetFields(Native.Flags).Single(field => field.FieldType == typeof(LaunchHost)).GetValue(service)!;
            watcher = (WatcherHost)Native.Field(typeof(ModWatcher), "Host").GetValue(null)!;
            tasks.SelectMinecraft(game);
            _ = FileTaskShell.Install(); // Genuine Main controller/controls for the original loader completion refresh.
            string folder = Path.Combine(game, "versions", "1.20.1"); Directory.CreateDirectory(folder);
            Put(Path.Combine(folder, "1.20.1.json"), "{\"id\":\"1.20.1\",\"type\":\"release\",\"releaseTime\":\"2023-06-12T13:25:51Z\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[],\"downloads\":{},\"arguments\":{\"game\":[\"--server\",\"discard.invalid\",\"--port\",\"25565\"],\"jvm\":[]}}");
            using (ZipArchive zip = ZipFile.Open(Path.Combine(folder, "1.20.1.jar"), ZipArchiveMode.Create))
            using (StreamWriter version = new(zip.CreateEntry("version.json").Open())) version.Write("{\"id\":\"1.20.1\",\"name\":\"1.20.1\",\"release_target\":\"1.20.1\"}");
            Put(Path.Combine(folder, "PCL", "Setup.ini"), "VersionArgumentIndieV2:True\r\n");
            instance = new(folder);
            foreach (Settings.Setting entry in Settings.Entries.Values)
                if (entry.Source == Settings.Sources.Instance) Cache(entry, instance.PathVersion, entry.DefaultValue!);
            SetCache("VersionArgumentIndieV2", true, true); SetCache("VersionPersonalOptionsSync", 0, true); SetCache("VersionWorldBackupAutoEnabled", false, true);
            foreach (string flag in new[] { "LaunchPersonalOptionsSync", "LaunchPersonalOptionsKeys", "LaunchRecentWorlds" }) SetCache(flag, true);
            SetCache("LaunchAdvanceGraphicCard", false); SetCache("LaunchSkinType", 0); SetCache("LaunchArgumentWindowType", 1);
            InitialLaunchCount = Settings.Get<int>("SystemLaunchCount"); SetCache("SystemLaunchCount", InitialLaunchCount);
            InstallLaunchCountStore();
            SetCache("LaunchArgumentVisible", 3); SetCache("LaunchArgumentPriority", 1); SetCache("UiMusicStop", false); SetCache("UiMusicStart", false);
            Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            options = Path.Combine(instance.PathIndie, "options.txt"); Put(options, "lang:en_us\r\nkey_key.forward:key.keyboard.w\r\nfixtureUntouched:stable\r\n");
            Put(Path.Combine(instance.PathIndie, "saves", "ControlledWorld", "level.dat"), "owned synthetic world marker; not a Minecraft NBT validation");
            Native.Static("PCL.LocalGameData", "WriteJson", "personal-options.json", new JObject(new JProperty("version", 1), new JProperty("values", new JObject(new JProperty("key_key.forward", "key.keyboard.k")))));
            fakeJava = Path.Combine(root, "selected-java", "java.exe"); Directory.CreateDirectory(Path.GetDirectoryName(fakeJava)!);
            ModLaunch.McLaunchJavaSelected = new Java(Path.GetDirectoryName(fakeJava)!, new Version(21, 0, 1)) { FileEncodingName = "UTF-8", NativeEncodingName = "UTF-8" };
            Native.Field(typeof(ModLaunch), "McLaunchEncoding").SetValue(null, new UTF8Encoding(false));
            ModLaunch.McLoginLoader = new("Unselected synthetic login boundary", _ => Forbidden("login workload"))
            { Input = new ModLaunch.McLoginLegacy { UserName = "owned-fixture", SkinType = 0, SkinName = "" },
              Output = new ModLaunch.McLoginResult { Name = "owned-fixture", Uuid = new string('0', 32), Type = "Legacy", AccessToken = "", ClientToken = "", ProfileJson = "{}" } };
            ModLaunch.CurrentLaunchOptions = new() { Instance = instance, ServerIp = "", QuickPlayWorld = "ControlledWorld" };
            prepared = ModLaunch.CurrentLaunchOptions;
            command = "/d /q /c launch-owned.cmd"; Native.Field(typeof(ModLaunch), "McLaunchArgument").SetValue(null, command);
            string marker = Guid.NewGuid().ToString("N"); OutputMarker = "PCLLF_CONTROLLED_STDOUT_" + marker; ErrorMarker = "PCLLF_CONTROLLED_STDERR_" + marker;
            InstallObservers(); InstallCrashBoundary();
        }
        catch (Exception error) { RecordPrimary(error); Dispose(); throw; }
    }

    private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private void SaveStatic(Type owner, string name) { FieldInfo field = Native.Field(owner, name); object? before = field.GetValue(null); restore.Push(() => field.SetValue(null, before)); }
    private void Set(object owner, string name, object value) { FieldInfo field = Native.Field(owner.GetType(), name); object? before = field.GetValue(owner); restore.Push(() => field.SetValue(owner, before)); field.SetValue(owner, value); }
    private void Cache(Settings.Setting entry, string key, object value) { bool had = entry.ValueCache.TryGetValue(key, out object? before); restore.Push(() => { if (had) entry.ValueCache[key] = before!; else entry.ValueCache.TryRemove(key, out _); }); entry.ValueCache[key] = value; }
    private void SetCache(string name, object value, bool perInstance = false) => Cache(Settings.Entries[name], perInstance ? instance.PathVersion : "", value);
    private void InstallLaunchCountStore()
    {
        // LF's real setting is encrypted Int32 in the open-source private
        // Registry namespace. The platform leaf stores its REG_SZ bytes only;
        // original Settings performs encryption, conversion, cache and +1.
        Settings.Setting entry = Settings.Entries["SystemLaunchCount"];
        Native.Check(entry.Key == "SystemLaunchCount" && entry.Source == Settings.Sources.Registry && entry.Encrypted &&
            entry.Type == typeof(int) && entry.DefaultValue is int value && value == 0,
            "Actual launch-count setting schema changed; refuse to widen Registry access.");
        string folder = (string)Native.Static("PCL.Settings", "RegistryFolderFor", entry)!;
        string key = (string)Native.Static("PCL.Settings", "RegistryValueName", entry)!;
        Native.Check(folder == "PCLDebug" && key == "SystemLaunchCount" &&
            Equals(Native.Field(Native.Type("PCL.ModSecret"), "RegFolder").GetRawConstantValue(), folder),
            "Actual LF launch-count Registry folder/key mapping changed.");
        string registry = @"HKEY_CURRENT_USER\Software\" + folder;
        object backend = Native.Field(typeof(Settings), "Backend").GetValue(null)!;
        object host = Native.Get(backend, "host")!;
        var read = (Func<string, string, string?, string?>)Native.Get(host, "ReadRegistry")!;
        var write = (Action<string, string, string?>)Native.Get(host, "WriteRegistry")!;
        launchCountStore = Path.Combine(root, "platform", "SystemLaunchCount.REG_SZ"); CheckOwned(launchCountStore);
        Native.Check(!File.Exists(launchCountStore), "Refusing an existing launch-count platform value.");
        Set(host, "ReadRegistry", new Func<string, string, string?, string?>((actualFolder, actualKey, fallback) =>
        {
            if (actualFolder != registry || actualKey != key) return read(actualFolder, actualKey, fallback); // Keep the original fail guard.
            lock (launchCountGate) { Interlocked.Increment(ref launchCountReads); return File.Exists(launchCountStore) ? File.ReadAllText(launchCountStore, Encoding.UTF8) : fallback; }
        }));
        Set(host, "WriteRegistry", new Action<string, string, string?>((actualFolder, actualKey, raw) =>
        {
            if (actualFolder != registry || actualKey != key) { write(actualFolder, actualKey, raw); return; } // Unknown keys remain forbidden.
            Native.Check(raw is not null, "Encrypted launch-count REG_SZ must be a string.");
            lock (launchCountGate) { Put(launchCountStore, raw!); Interlocked.Increment(ref launchCountWrites); }
        }));
        entry.Save(); // Genuine original SaveEntry produces the seed ciphertext.
        Native.Check(Volatile.Read(ref launchCountWrites) == 1 && File.Exists(launchCountStore), "Original launch-count SaveEntry did not persist its owned seed.");
    }
    internal void CheckLaunchCountPersisted(bool launched)
    {
        int expected = checked(InitialLaunchCount + (launched ? 1 : 0));
        Native.Check(Volatile.Read(ref launchCountWrites) == (launched ? 2 : 1), "Original launch End did not make exactly its one additional platform write.");
        string raw;
        lock (launchCountGate) raw = File.ReadAllText(launchCountStore!, Encoding.UTF8);
        Native.Check(raw.Length > 0 && raw != expected.ToString(System.Globalization.CultureInfo.InvariantCulture), "Original launch-count crypto wrote a plaintext or empty platform value.");
        int reads = Volatile.Read(ref launchCountReads);
        Settings.Entries["SystemLaunchCount"].ValueCache.TryRemove("", out _);
        object actual = Settings.Get("SystemLaunchCount");
        Native.Check(actual is int count && count == expected && Settings.Get<int>("SystemLaunchCount") == expected && Volatile.Read(ref launchCountReads) == reads + 1,
            "Original Settings cold Registry read/decryption did not round-trip the launch-count Int32.");
    }

    private void Forbidden(string name) { Interlocked.Increment(ref forbidden); throw new InvalidOperationException("Unselected launch-process boundary: " + name); }
    private void Guard(object owner, string name)
    {
        FieldInfo field = Native.Field(owner.GetType(), name); MethodInfo invoke = field.FieldType.GetMethod("Invoke")!;
        var args = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        Action fail = () => Forbidden(name);
        Set(owner, name, Expression.Lambda(field.FieldType, Expression.Block(Expression.Invoke(Expression.Constant(fail)), Expression.Default(invoke.ReturnType)), args).Compile());
    }

    internal void Prepare()
    {
        Native.Check(instance.Version.Valid && instance.Version.Vanilla.Major == 20 && instance.ReleaseTime.Year == 2023, "Fixture did not pass the actual version JSON/JAR parser.");
        List<string> arguments = (List<string>)Native.Static("PCL.ModLaunch", "McLaunchArgumentsGame")!;
        Native.Check(!arguments.Contains("--server") && !arguments.Contains("--port") && arguments.Contains("--quickPlaySingleplayer") && arguments.Contains("ControlledWorld") &&
            arguments.Count(value => value == "--quickPlayPath") == 1 && prepared.QuickPlay is not null, "Actual game argument pipeline did not replace server arguments and prepare its real Quick Play session.");
        Native.Static("PCL.ModLaunch", "McLaunchPrerun");
        Native.Check(prepared.PersonalOptions is { Tracked: false } && File.ReadAllText(options).Contains("key_key.forward:key.keyboard.k\r\n", StringComparison.Ordinal) &&
            File.ReadAllText(options).Contains("fixtureUntouched:stable\r\n", StringComparison.Ordinal), "Actual prerun did not merge the real personal profile while preserving unrelated options.");
        string log = prepared.QuickPlay!.LogFile!;
        CheckOwned(options); CheckOwned(log);
        Put(Path.Combine(instance.PathIndie, "launch-owned.cmd"), "@echo off\r\nsetlocal DisableDelayedExpansion\r\nset /p PCLLF_BOOT=\r\necho " + OutputMarker + "\r\n1>&2 echo " + ErrorMarker + "\r\necho Setting user: owned-fixture\r\necho lwjgl version synthetic\r\necho OpenAL initialized\r\necho Created textures owned-atlas\r\nset /p PCLLF_FINISH=\r\n>\"%PCLLF_OPTIONS%\" echo lang:en_us\r\n>>\"%PCLLF_OPTIONS%\" echo key_key.forward:key.keyboard.i\r\n>>\"%PCLLF_OPTIONS%\" echo fixtureUntouched:stable\r\n>\"%PCLLF_QUICKLOG%\" echo [{\"type\":\"singleplayer\",\"id\":\"ControlledWorld\",\"name\":\"Controlled fixture\",\"lastPlayedTime\":\"2026-01-01T00:00:00Z\"}]\r\nexit /b " + exitCode + "\r\n");
    }

    internal void Start()
    {
        List<LegacyLaunchStep> steps = [new LegacyLaunchProcessStep { Name = "启动进程", Work = service.McLaunchRun, ProgressWeight = 2 },
            new LegacyLaunchWaitStep { Name = "等待游戏窗口出现", Work = service.McLaunchWait },
            new LegacyLaunchIntStep { Name = "结束处理", Work = _ => service.McLaunchEnd() }];
        LegacyLaunchOperation operation = (LegacyLaunchOperation)Native.Static("PCL.ModLaunch", "CreateLaunchCombo", "Owned process/watcher contract", steps)!;
        Plan = (ModLoader.LoaderCombo<object>)operation.Source;
        Native.Check(Plan.GetType().Assembly == Native.Main && Plan.Loaders.Count == 3 && Plan.Loaders[0] is ModLoader.LoaderTask<int, Process> &&
            Plan.Loaders[1] is ModLoader.LoaderTask<Process, int> && Plan.Loaders[2] is ModLoader.LoaderTask<int, int>, "Launch steps did not use Main's real typed loader adapters.");
        foreach (ModLoader.LoaderBase task in Plan.Loaders) ObserveTask(task);
        ModLoader.LoaderTaskbar.Add(Plan);
        // The initial Combo Update can queue a real task whose delegate has
        // not entered yet. Hold startup live before calling the real scheduler.
        Interlocked.Exchange(ref startRequested, 1); Interlocked.Exchange(ref firstStartReservation, 1);
        try { operation.Start(); } catch { schedulingFailed = true; throw; }
    }

    internal void PrepareClosingNotice()
    {
        Prepare();
        string file = Path.Combine(instance.PathIndie, "launch-owned.cmd"); CheckOwned(file);
        string script = File.ReadAllText(file);
        script = script.Replace("set /p PCLLF_FINISH=\r\n", "set /p PCLLF_FINISH=\r\nif /i \"%PCLLF_FINISH%\"==\"closing\" (\r\necho Someone is closing me!\r\nset /p PCLLF_AFTER_NOTICE=\r\n)\r\n", StringComparison.Ordinal);
        Put(file, script);
    }

    internal void CheckLaunchProtection()
    {
        Native.Check(service.GetLaunchBlockReason() is null, "A stopped owned process left a launch-blocking task.");
        Func<bool> original = launch.WorldBackupsBusy;
        try {
            launch.WorldBackupsBusy = () => true;
            string reason = service.GetLaunchBlockReason() ?? "";
            Native.Check(reason.Contains("存档", StringComparison.Ordinal) && !reason.Contains("被修改", StringComparison.Ordinal),
                "Backup work is still reported as an unexplained version modification.");
        } finally { launch.WorldBackupsBusy = original; }
        using ManualResetEventSlim entered = new(false), release = new(false), stopped = new(false);
        MeloongCore.RedoableWorker worker = new(() => { entered.Set(); release.Wait(); });
        Native.Check(ModMinecraft.McInstanceLock.TryAcquire(worker, instance.PathVersion), "Could not acquire the owned file-task reservation.");
        worker.Stopped += stopped.Set;
        try {
            worker.Start(); Native.Check(entered.Wait(5000), "Owned file-task worker did not enter.");
            Native.Check(worker.Running && (service.GetLaunchBlockReason() ?? "").Contains("文件任务", StringComparison.Ordinal),
                "Exited-game cleanup bypassed an actual active file-task reservation.");
        } finally {
            release.Set(); Native.Check(stopped.Wait(5000), "Owned file-task worker did not finish its reservation.");
        }
        Native.Check(service.GetLaunchBlockReason() is null, "Completed file-task reservation remained locked.");
    }

    private void ObserveTask(ModLoader.LoaderBase task)
    {
        FieldInfo field = Native.Field(task.GetType(), "LoadDelegate"); Delegate work = (Delegate)field.GetValue(task)!;
        MethodInfo invoke = field.FieldType.GetMethod("Invoke")!; var argument = Expression.Parameter(invoke.GetParameters()[0].ParameterType);
        Action<Action> observe = action =>
        {
            threads.TryAdd(Thread.CurrentThread, 0); CaptureNativeThreads(); Interlocked.Increment(ref generation);
            if (ReferenceEquals(task, Plan.Loaders[0])) Interlocked.Exchange(ref firstStartReservation, 0);
            action();
        };
        field.SetValue(task, Expression.Lambda(field.FieldType, Expression.Invoke(Expression.Constant(observe), Expression.Lambda<Action>(Expression.Invoke(Expression.Constant(work, field.FieldType), argument))), argument).Compile());
    }

    private void CaptureNativeThreads()
    {
        if (Plan is null) return;
        foreach (ModLoader.LoaderBase task in Plan.Loaders)
            if (Native.Get(task, "LastRunningThread") is Thread thread && threads.TryAdd(thread, 0)) Interlocked.Increment(ref generation);
    }

    private void CheckOwned(string path)
    {
        Native.Check(Path.IsPathFullyQualified(path) && Native.Full(path).StartsWith(Native.Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            path.IndexOfAny(['"', '\r', '\n', '%', '!']) < 0, "Command fixture path escaped its GUID owner or contains shell expansion characters.");
    }

    internal Dictionary<string, string> ProfileSnapshot() => Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(profile, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
    internal bool SameProfile(Dictionary<string, string> before) { Dictionary<string, string> after = ProfileSnapshot(); return before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? value) && pair.Value == value); }
    internal bool HasSessionFiles() => new[] { "options-sessions", "quickplay-sessions", "world-backup-sessions" }.Any(name => Directory.Exists(Path.Combine(profile, name)) && Directory.EnumerateFiles(Path.Combine(profile, name)).Any());
    internal void CheckTracked()
    {
        Native.Check(prepared.PersonalOptions.Tracked && HasSessionFiles() &&
            File.Exists(Path.Combine(profile, "options-sessions", prepared.PersonalOptions.Id + ".json")) &&
            File.Exists(Path.Combine(profile, "quickplay-sessions", prepared.QuickPlay.Id + ".json")), "BeforeMonitoring did not persist both real local journals for the owned PID.");
        foreach (string folder in new[] { "options-sessions", "quickplay-sessions", "world-backup-sessions" })
        {
            string file = Directory.GetFiles(Path.Combine(profile, folder), "*.json").Single(); JObject entry = JObject.Parse(File.ReadAllText(file));
            Native.Check(entry["pid"]!.ToObject<int>() == Child.Id && entry["started"]!.ToObject<long>() == Child.StartTime.ToUniversalTime().Ticks,
                "Actual journal did not retain the owned process PID/start-time pair.");
        }
    }
    internal void CheckFinished(bool healthy)
    {
        Native.Check(!HasSessionFiles() && !File.Exists(prepared.QuickPlay.LogFile), "Actual exit callbacks left tracked journals or Quick Play log behind.");
        JObject personal = (JObject)Native.Static("PCL.LocalGameData", "ReadJson", "personal-options.json")!;
        JObject recent = (JObject)Native.Static("PCL.LocalGameData", "ReadJson", "recent-worlds.json")!;
        Native.Check(personal["values"]?["key_key.forward"]?.ToString() == "key.keyboard.i" &&
            File.ReadAllText(options).Contains("fixtureUntouched:stable", StringComparison.Ordinal), "Actual Finish did not capture the controlled process's edited keybinding or preserve unrelated data.");
        Native.Check(((JArray)recent["worlds"]!).OfType<JObject>().Any(world => world["identifier"]?.ToString() == "ControlledWorld" &&
            world["name"]?.ToString() == "Controlled fixture" && world["versionPath"]?.ToString() == instance.PathVersion), "Actual Quick Play Finish did not import the owned process's world record.");
        var points = (List<JObject>)Native.Static("PCL.EnvironmentHistory", "Points", instance.PathVersion)!;
        Native.Check(!string.IsNullOrEmpty(prepared.EnvironmentPoint) && points.Single(point => point["id"]!.ToString() == prepared.EnvironmentPoint)["healthy"]!.ToObject<bool>() == healthy,
            "Actual capture/MarkHealthy did not preserve the normal-versus-abnormal environment checkpoint contract.");
    }
}
