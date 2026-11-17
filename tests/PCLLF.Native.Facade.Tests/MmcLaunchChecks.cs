using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;
using WatcherHost = PCLCS.LegacyWatcherHost<PCL.ModWatcher.Watcher, PCL.ModMinecraft.McInstance, PCL.ModLoader.LoaderTask<System.Diagnostics.Process, int>, PCL.ModWatcher.Watcher.MinecraftState>;

namespace PCLLF.Native.Facade.Tests;

// Original LF host tests; repository LICENCE. Explicit opt-in, self-authored Java only.
internal static partial class MmcLaunchChecks {
    internal const string Group = "mmc-launch-replay";
    private const string ChildSwitch = "--mmc-launch-replay-child", Receipt = "mmc-launch-replay.done";
    private static Scope? retained;
    internal static void Run(FixtureEnvironment environment) => RunCore(environment, false);
    internal static void RunVariants(FixtureEnvironment environment) => RunCore(environment, true);
    private static void RunCore(FixtureEnvironment environment, bool variants) {
        if (!Environment.GetCommandLineArgs().Contains(ChildSwitch, StringComparer.Ordinal)) { RunChild(variants ? "mmc-variant-launch" : Group); return; }
        try {
            ModpackUpdateCleanup.VerifyIsolatedChild(); retained = new(environment, variants);
            if (variants) retained.RunVariants(); else retained.Run();
            environment.AssertNoForbiddenIo();
            Native.Check(System.Windows.Application.Current.Windows.Count == 0, "MMC replay opened a window.");
            File.WriteAllText(Path.Combine(environment.Root, Receipt), MainHash());
            Console.WriteLine(variants
                ? "PASS mmc-variant-launch: actual Main declines without mutation, explicitly adopts Java17 once, launches twice without another generation or prompt; self-authored JVM only."
                : "PASS mmc-launch-replay: actual Main executor, Java8 wrapper, exact argv/cwd, pre-storage secret/frame redaction, twice-start generation, Start/Wait cancellation, typed receipts, actual exit/pin/mutable cleanup. Self-authored JVM only; no Minecraft acceptance.");
            Console.Out.Flush(); Console.Error.Flush(); using ManualResetEventSlim keepGuards = new(); keepGuards.Wait();
        } catch (Exception error) {
            retained?.StopOwnedChildren();
            File.WriteAllText(Path.Combine(environment.Root, Receipt + ".failed"), error.ToString());
            ModpackUpdateCleanup.TerminateChild("MMC replay failed with guards retained: " + error);
        }
    }
    private sealed partial class Scope {
        private readonly FixtureEnvironment environment;
        private readonly FileTaskScope tasks;
        private readonly PackRootInstallChecksLoopback server = new();
        private readonly HttpClient client = new();
        private readonly ConcurrentQueue<Thread> watcherThreads = new();
        private readonly ConcurrentQueue<Exception> asyncErrors = new();
        private readonly ConcurrentDictionary<Guid, Process> children = new();
        private readonly ConcurrentQueue<string> diagnostics = new();
        private readonly string root, game, version, java8, java17, javac, control;
        private readonly string secret = "owned-synthetic-access-" + Guid.NewGuid().ToString("N");
        private readonly MmcJavaPinStore pins;
        private ModMinecraft.McInstance instance = null!;
        private ModLoader.LoaderCombo<object> plan = null!;
        private bool cancelAtSpawn;
        private bool expectedFailure;
        private int starts, forbidden, stoppedNotifications;
        private Guid currentSession;
        private const string ProbeSource = """
            package fixture;
            import java.io.*;
            import java.nio.file.*;
            import java.util.*;
            public final class Main {
                private static void out(String key, String value) throws Exception {
                    System.out.println(key + "=" + Base64.getEncoder().encodeToString(value.getBytes("UTF-8")));
                }
                public static void main(String[] args) throws Exception {
                    String control = args[0];
                    out("CWD", new File(".").getCanonicalPath());
                    out("TMP", System.getProperty("java.io.tmpdir"));
                    out("EXTRACT", System.getProperty("org.lwjgl.system.SharedLibraryExtractPath"));
                    for (int i=0;i<args.length;i++) out("ARG"+i, i==1 ? "<account>" : args[i]);
                    String command = System.getProperty("sun.java.command", "");
                    System.out.println("FRAME=" + command.substring(command.lastIndexOf(' ') + 1));
                    System.out.println("SECRET=" + args[1]);
                    Files.write(Paths.get(System.getProperty("java.io.tmpdir"), "owned-temp"), new byte[]{1});
                    Files.write(Paths.get(System.getProperty("org.lwjgl.system.SharedLibraryExtractPath"), "owned-native"), new byte[]{2});
                    System.out.println("PROBE_READY"); System.out.flush();
                    while (!Files.exists(Paths.get(control + ".ready"))) Thread.sleep(10);
                    if (control.endsWith("-early-exit")) {
                        System.err.println("Owned fixture exits before startup completes");
                        System.exit(23);
                    }
                    System.out.println("Setting user: owned-fixture");
                    System.out.println("lwjgl version controlled");
                    System.out.println("OpenAL initialized");
                    System.out.println("Created textures controlled-atlas"); System.out.flush();
                    while (!Files.exists(Paths.get(control + ".exit"))) Thread.sleep(10);
                    System.out.println("Someone is closing me!"); System.out.flush();
                    Thread.sleep(400);
                }
            }
            """;
        internal Scope(FixtureEnvironment environment, bool variants = false) {
            this.environment = environment; tasks = new(environment);
            root = Path.Combine(tasks.Root, Group); game = Path.Combine(root, "minecraft"); version = Path.Combine(game, "versions", "controlled-mmc"); control = Path.Combine(root, "control");
            Directory.CreateDirectory(root); Directory.CreateDirectory(game);
            java8 = Tool("PCLLF_MMC_TEST_JAVA8"); java17 = Tool("PCLLF_MMC_TEST_JAVA17"); javac = Path.Combine(Path.GetDirectoryName(java17)!, "javac.exe");
            Native.Check(File.Exists(javac), "PCLLF_MMC_TEST_JAVA17 must identify a JDK with javac.exe.");
            ConfigProviders.Default = new JsonConfigProvider(Path.Combine(root, "private-config.json"));
            Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java8)!)])); Configs.JavaConfigVersion.Set(Versions.JavaConfigVersion);
            pins = new(Path.Combine(root, "java-pins"));
            Native.Property(typeof(MmcLaunch), "JavaPins").SetValue(null, new Func<MmcJavaPinStore>(() => pins));
            var mutableAuthority = (MmcMutableRuntimeAuthority)typeof(MmcMutableRuntimeAuthority)
                .GetMethod("ForOwnedFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [environment.Root])!;
            Native.Property(typeof(MmcLaunch), "MutableRuntimeAuthorityFactory").SetValue(null, new Func<MmcMutableRuntimeAuthority>(() => mutableAuthority));
            Native.Property(typeof(MmcLaunch), "ProcessCreated").SetValue(null, new Action<Guid, Process>((id, process) => {
                Native.Check(children.TryAdd(id, process), "MMC executor reused a typed session identity.");
                currentSession = id; Interlocked.Increment(ref starts);
                Native.Check(File.Exists(Path.Combine(version, "PCL", "MmcSessions", id.ToString("N"), "Running.record")), "Process observer ran before genuine Running registration.");
                bool blocked = false; try { using var maintenance = pins.EnterMaintenance(Path.GetDirectoryName(Path.GetDirectoryName(process.StartInfo.FileName)!)!, TimeSpan.Zero); } catch (IOException) { blocked = true; }
                Native.Check(blocked, "Running Java is not protected by a genuine durable pin.");
                if (cancelAtSpawn) plan.Cancel();
            }));
            tasks.SelectMinecraft(game); _ = FileTaskShell.Install();
            foreach (var pair in new Dictionary<string, object> {
                ["LaunchAdvanceJvm"]="", ["LaunchAdvanceGame"]="", ["LaunchAdvanceRun"]="", ["LaunchAdvanceRunWait"]=true,
                ["LaunchAdvanceDisableJLW"]=false, ["LaunchArgumentJavaSelect"]="", ["LaunchRamType"]=1, ["LaunchRamCustom"]=0,
                ["LaunchArgumentWindowType"]=1, ["LaunchArgumentWindowWidth"]="854", ["LaunchArgumentWindowHeight"]="480",
                ["LaunchPersonalOptionsSync"]=false, ["LaunchPersonalOptionsKeys"]=false, ["LaunchRecentWorlds"]=false,
                ["UiMusicStop"]=false, ["UiMusicStart"]=false, ["LaunchArgumentVisible"]=1 }) Native.Cache(pair.Key, pair.Value);
            InstallWatcherBoundaries(); GuardOrdinaryLaunch();
            byte[] jar = CompileProbe(); byte[] assets = Encoding.UTF8.GetBytes("{\"objects\":{}}"); server.Add("/index", assets);
            string archive = Path.Combine(root, "owned-mmc.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
                Add(zip, "pack/mmc-pack.json", Encoding.UTF8.GetBytes("""{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}"""));
                Add(zip, "pack/instance.cfg", Encoding.UTF8.GetBytes("name=Controlled MMC\nOverrideMemory=true\nMinMemAlloc=128\nMaxMemAlloc=256\nOverrideWindow=true\nMinecraftWinWidth=854\nMinecraftWinHeight=480\n"));
                string patch = $$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Main","libraries":[],"mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"fixture.jar"},"assetIndex":{"id":"owned","url":"{{server.Url("/index")}}","sha1":"{{Convert.ToHexStringLower(SHA1.HashData(assets))}}","size":{{assets.Length}}},"compatibleJavaMajors":[8]}""";
                if (variants) patch = patch.Replace("\"compatibleJavaMajors\":[8]", "\"compatibleJavaMajors\":[8,17]")
                    .Replace("\"libraries\":[]", "\"libraries\":[{\"name\":\"fixture:bootstrap:1\",\"MMC-hint\":\"local\",\"MMC-filename\":\"bootstrap.jar\"}]");
                Add(zip, "pack/patches/net.minecraft.json", Encoding.UTF8.GetBytes(patch)); Add(zip, "pack/libraries/fixture.jar", jar);
                if (variants) Add(zip, "pack/libraries/bootstrap.jar", jar);
                Add(zip, "pack/minecraft/options.txt", Encoding.UTF8.GetBytes("lang:en_us\n"));
            }
            var metadata = new MmcMetadataStore(Path.Combine(game, "PCL", "MmcCache", "metadata"), new Uri(server.Url("/metadata/")), client);
            var backend = new MmcInstanceBackend<MmcArtifactDownloadSpec>(metadata,
                new(Path.Combine(game, "libraries"), Path.Combine(game, "assets"), Path.Combine(game, "PCL", "MmcCache", "sources"), Path.Combine(game, "versions")), spec => spec,
                async (files, token) => { foreach (var file in files) { Native.Check(file.Urls.All(server.Owns), "Replay source escaped loopback."); await File.WriteAllBytesAsync(file.DownloadPath, await client.GetByteArrayAsync(file.Urls[0], token), token); } });
            var imported = backend.ImportAsync(new(archive, version, "controlled-mmc", new("windows", "x86_64", 8))).GetAwaiter().GetResult();
            Native.Check(imported.Status == MmcInstanceBackendStatus.Succeeded, "Real backend import failed: " + imported.Failure);
            Native.Check(server.Requested("/index"), "Real import did not materialize its owned asset input.");
            instance = new(version); Native.Field(typeof(ModMinecraft), "_McInstanceSelected").SetValue(null, instance);
            Settings.Set("VersionPersonalOptionsSync", 0, instance); Settings.Set("VersionWorldBackupAutoEnabled", false, instance);
            instance.Config.Save();
            ModLaunch.McLoginLoader = new("Owned account boundary", _ => throw new IOException("Replay attempted account IO")) {
                Output = new() { Name="owned-fixture", Uuid=new string('0',32), AccessToken=secret, ClientToken="owned-client", Type="Legacy", ProfileJson="{}" },
                State = ModBase.LoadState.Finished
            };
        }
        internal void Run() {
            Launch("first", false, false); Launch("second", false, false); Launch("cancel-wait", false, true); Launch("cancel-start", true, false);
            Launch("early-exit", false, false, expectEarlyExit: true);
            Native.Check(starts == 5 && forbidden == 0 && !MmcLaunch.HasActiveSessions, "Replay missed a process or crossed the ordinary launch chain.");
            Native.Check(MmcInstanceInspector.Read(version).Definition!.Generation == 1, "Normal launches or cancellation changed the adopted generation.");
            Until(() => watcherThreads.All(thread => !thread.IsAlive), "all genuine watcher threads to exit");
            Native.Check(asyncErrors.IsEmpty, "Watcher background primitive failed: " + string.Join(";", asyncErrors));
            InstanceConfigProviders.FlushAndDisposeWithin(root); server.Dispose(); client.Dispose(); environment.AssertNoForbiddenIo();
        }
        internal void RunVariants() {
            Configs.JavaList.Set(new ConcurrentList<Java>([new(Path.GetDirectoryName(java8)!), new(Path.GetDirectoryName(java17)!)]));
            MmcLaunchPreferences.SetForcedJava(instance, new Java(Path.GetDirectoryName(java17)!)); instance.Config.Save();
            string primary = Path.Combine(version, "PCL", "InstanceDefinition.json"); byte[] original = File.ReadAllBytes(primary);
            int confirmations = 0;
            Native.Property(typeof(MmcLaunch), "ConfirmVariantAdoption").SetValue(null, new Func<ModMinecraft.McInstance, MmcLaunchJavaSelection, bool>((_, target) => {
                confirmations++; Native.Check(target.RuntimeJava.Major == 17 && target.RequiresVariantAdoption, "Confirmation did not describe the actual target Java."); return false;
            }));
            Launch("declined", false, false, expectNoProcess: true);
            Native.Check(confirmations == 1 && starts == 0 && original.SequenceEqual(File.ReadAllBytes(primary)), "Declining adoption changed the published instance or created a process.");
            Native.Property(typeof(MmcLaunch), "ConfirmVariantAdoption").SetValue(null, new Func<ModMinecraft.McInstance, MmcLaunchJavaSelection, bool>((_, _) => { confirmations++; return true; }));
            Launch("adopted17", false, false, expectedGeneration: 2);
            Launch("existing17", false, false, expectedGeneration: 2);
            Native.Check(confirmations == 2 && starts == 2, "Existing adopted Java prompted again or did not launch twice.");
            Until(() => watcherThreads.All(thread => !thread.IsAlive), "variant watcher threads to exit");
            InstanceConfigProviders.FlushAndDisposeWithin(root); server.Dispose(); client.Dispose(); environment.AssertNoForbiddenIo();
        }
        internal void StopOwnedChildren() {
            foreach (Process process in children.Values) {
                try { if (!process.HasExited) process.Kill(true); process.WaitForExit(5000); }
                catch (InvalidOperationException) { }
            }
        }
        private void Launch(string name, bool startCancel, bool waitCancel, long expectedGeneration = 1, bool expectNoProcess = false, bool expectEarlyExit = false) {
            expectedFailure = expectEarlyExit;
            string run = control + "-" + name; cancelAtSpawn = startCancel; currentSession = Guid.Empty;
            ModLaunch.McLaunchProcess = null!; ModLaunch.McLaunchWatcher = null!;
            List<string> expected = [run, secret, "", "中文 空格", "C:\\中文 tail\\", "repeat", "repeat"];
            var options = new ModLaunch.McLaunchOptions { Instance=instance, ExtraGameArgs=expected };
            ModLaunch.CurrentLaunchOptions = options;
            var steps = MmcLaunch.CreateSteps(instance, options, java => ModLaunch.McLaunchJavaSelected=java);
            var operation = (LegacyLaunchOperation)Native.Static("PCL.ModLaunch", "CreateLaunchCombo", "Owned MMC replay " + name, steps)!;
            int previouslyStopped = Volatile.Read(ref stoppedNotifications);
            plan = (ModLoader.LoaderCombo<object>)operation.Source; ModLaunch.McLaunchLoaderReal = plan; ModLoader.LoaderTaskbar.Add(plan); operation.Start();
            if (expectNoProcess) {
                Until(() => plan.State != ModBase.LoadState.Loading, "declined variant finishes without a child");
                Native.Check(plan.State == ModBase.LoadState.Canceled && currentSession == Guid.Empty, "Declined adoption was not canceled before process creation."); return;
            }
            Until(() => currentSession != Guid.Empty && ModLaunch.McLaunchWatcher is not null || plan.State == ModBase.LoadState.Failed, "actual Main MMC child and watcher");
            if (plan.State == ModBase.LoadState.Failed) throw new IOException("MMC executor failed: " + plan.Error);
            Process child = children[currentSession]; Guid id = currentSession;
            Native.Check(ReferenceEquals(child, ModLaunch.McLaunchProcess) && ReferenceEquals(child, ModLaunch.McLaunchWatcher.GameProcess), "Session, Main and Watcher refer to different Java processes.");
            if (!startCancel) {
                Until(() => ModLaunch.McLaunchWatcher.LatestLog.Contains("PROBE_READY"), "actual Java probe output");
                var lines = ModLaunch.McLaunchWatcher.LatestLog.ToArray();
                Native.Check(Decode(lines,"CWD") == Native.Full(instance.PathIndie), "Java working directory is not managed GameRoot.");
                for (int i=0;i<expected.Count;i++) Native.Check(Decode(lines,"ARG"+i) == (i==1 ? "<account>" : expected[i]), "Final Java argv differs at index " + i);
                string tmp = Decode(lines,"TMP"), extract=Decode(lines,"EXTRACT");
                Native.Check(tmp.Contains(id.ToString("N"),StringComparison.Ordinal) && extract.Contains(id.ToString("N"),StringComparison.Ordinal) && Native.Full(tmp).StartsWith(environment.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Native.Full(extract).StartsWith(environment.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase), "Mutable paths lost their typed session identity or owned fixture authority.");
                Native.Check(File.Exists(Path.Combine(tmp,"owned-temp")) && File.Exists(Path.Combine(extract,"owned-native")), "Controlled Java did not use the owned mutable directories.");
                // Observe the actual received frame, without disclosing it in any failure/log.
                string frame = child.StartInfo.ArgumentList.Last();
                foreach (string line in ModLaunch.McLaunchWatcher.LatestLog.Concat(ModLaunch.McLaunchWatcher.WaitingLog).Where(line=>line is not null))
                    Native.Check(!line.Contains(secret,StringComparison.Ordinal) && !line.Contains(frame,StringComparison.Ordinal), "Watcher stored sensitive account data or an encoded frame before redaction.");
                Native.Check(lines.Any(line=>line.StartsWith("FRAME=",StringComparison.Ordinal)) && lines.Any(line=>line.StartsWith("SECRET=",StringComparison.Ordinal)), "Probe did not exercise both sensitive log forms.");
                if (expectEarlyExit) {
                    File.WriteAllText(run + ".ready", "");
                    Until(() => plan.State == ModBase.LoadState.Failed, "actual early-exit failure reaches the launch task");
                    string failure = plan.Error?.ToString() ?? "";
                    Native.Check(failure.Contains("Java 8", StringComparison.Ordinal) && failure.Contains("23（0x00000017）", StringComparison.Ordinal) &&
                        failure.Contains(Path.Combine(instance.PathIndie, "logs"), StringComparison.OrdinalIgnoreCase) && !failure.Contains(secret, StringComparison.Ordinal),
                        "Early-exit failure lost its actual Java, exit status or game log location, or exposed account data.");
                }
                else if (waitCancel) { Native.Check(ModLaunch.McLaunchWatcher.State == ModWatcher.Watcher.MinecraftState.Loading,"Wait cancellation missed Loading state."); plan.Cancel(); }
                else { File.WriteAllText(run+".ready",""); Until(()=>plan.State==ModBase.LoadState.Finished,"real log-driven startup completion"); File.WriteAllText(run+".exit",""); }
            }
            Until(()=>child.HasExited && !MmcLaunch.HasActiveSessions,"actual exit and session completion");
            Native.Check(Volatile.Read(ref stoppedNotifications) == previouslyStopped + 1, "Verified exit did not complete exactly one real Watcher stop notification.");
            if (expectEarlyExit) Native.Check(child.ExitCode == 23 && ModLaunch.McLaunchWatcher.State == ModWatcher.Watcher.MinecraftState.Crashed,
                "Early-exit fixture did not retain the actual nonzero child failure.");
            else if (!startCancel && !waitCancel) Native.Check(child.ExitCode==0 && ModLaunch.McLaunchWatcher.State==ModWatcher.Watcher.MinecraftState.Ended,"Normal JVM did not finish through real Watcher.");
            else Native.Check(plan.State is ModBase.LoadState.Canceled or ModBase.LoadState.Failed,"Canceled launch is still live or reported success.");
            string history=Path.Combine(version,"PCL","MmcSessionHistory",id.ToString("N"));
            Native.Check(File.Exists(Path.Combine(history,"Launching.record")) && File.Exists(Path.Combine(history,"Running.record")) && File.Exists(Path.Combine(history,"Exited.record")),"Confirmed exit did not archive all typed session records.");
            var receipt=ReadRecord<MmcSessionReceipt>(Path.Combine(history,"Launching.record")); var registration=ReadRecord<MmcSessionProcessRegistration>(Path.Combine(history,"Running.record"));
            Native.Check(receipt.SessionId==id && registration.SessionId==id && registration.ProcessId==child.Id && receipt.DefinitionGeneration==expectedGeneration,"Runtime/session/process identities drifted.");
            Native.Check(!Directory.Exists(Path.Combine(version,"PCL","MmcRuntime","sessions",id.ToString("N"))),"Actual exit left a live mutable native/temp tree.");
            Native.Check(!Directory.EnumerateFiles(Path.Combine(root,"java-pins"),"*.json",SearchOption.AllDirectories).Any(),"Verified exit retained an active durable Java pin.");
            using (pins.EnterMaintenance(Path.GetDirectoryName(Path.GetDirectoryName(child.StartInfo.FileName)!)!,TimeSpan.Zero)) { }
            Native.Check(MmcInstanceInspector.Read(version).Definition!.Generation==expectedGeneration,"Launch altered the adopted generation.");
        }
        private void InstallWatcherBoundaries() {
            var host=(WatcherHost)Native.Field(typeof(ModWatcher),"Host").GetValue(null)!;
            var stopped = host.ProcessStopped;
            host.ProcessStopped = (version, process, normal) => { stopped(version, process, normal); Interlocked.Increment(ref stoppedNotifications); };
            host.EnumWindows=(_,_)=>{}; host.RestoreLauncher=()=>{}; host.RefreshShutdownButton=()=>{}; host.CrashHint=()=>{}; host.FeedbackInfo=()=>{};
            host.LaunchLog=(text,_)=>diagnostics.Enqueue(text); host.CreateCrashAnalyzer=_=>new NoCrashUi();
            var original=host.RunInNewThread; host.RunInNewThread=(work,name)=>original(()=>{watcherThreads.Enqueue(Thread.CurrentThread); try {work();} catch(Exception error){asyncErrors.Enqueue(error);throw;}},name);
        }
        private void GuardOrdinaryLaunch() {
            object service=Native.Field(typeof(ModLaunch),"Service").GetValue(null)!;
            object host=service.GetType().GetFields(Native.Flags).Single(field=>field.FieldType.Name.StartsWith("LegacyLaunchHost`",StringComparison.Ordinal)).GetValue(service)!;
            foreach(string name in new[]{"ClientFix","SelectJava","StartProcess","Libraries","JsonRuleCheck"}) {
                FieldInfo field=Native.Field(host.GetType(),name); var invoke=field.FieldType.GetMethod("Invoke")!;
                var args=invoke.GetParameters().Select(p=>System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
                Action fail=()=>{Interlocked.Increment(ref forbidden);throw new IOException("MMC reached ordinary launch primitive "+name);};
                field.SetValue(host,System.Linq.Expressions.Expression.Lambda(field.FieldType,System.Linq.Expressions.Expression.Block(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(fail)),System.Linq.Expressions.Expression.Default(invoke.ReturnType)),args).Compile());
            }
        }
        private byte[] CompileProbe() {
            string source=Path.Combine(root,"source","fixture","Main.java"), classes=Path.Combine(root,"classes"); Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.CreateDirectory(classes); File.WriteAllText(source,ProbeSource,new UTF8Encoding(false));
            var start=new ProcessStartInfo(javac){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"--release","8","-encoding","UTF-8","-d",classes,source})start.ArgumentList.Add(argument);
            foreach(string key in new[]{"JAVA_TOOL_OPTIONS","JDK_JAVA_OPTIONS","_JAVA_OPTIONS","CLASSPATH"})start.Environment.Remove(key);
            using var process=Process.Start(start)!; var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(true); process.WaitForExit(5000); throw new TimeoutException("Controlled javac timed out."); }
            Native.Check(Task.WaitAll([output,error],5000),"Controlled javac output did not drain."); Native.Check(process.ExitCode==0,"Controlled javac failed: "+error.Result);
            using MemoryStream bytes=new(); using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))Add(zip,"fixture/Main.class",File.ReadAllBytes(Path.Combine(classes,"fixture","Main.class")));return bytes.ToArray();
        }
        private void Until(Func<bool> complete,string message) {
            DateTime deadline=DateTime.UtcNow.AddSeconds(25);
            while(!complete()) { Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline); if(!expectedFailure && plan is not null && plan.State==ModBase.LoadState.Failed)throw new IOException("MMC replay failed during "+message,plan.Error); if(DateTime.UtcNow>=deadline)throw new TimeoutException("MMC replay timed out during "+message+"; plan="+plan?.State+"; steps="+string.Join(";",plan?.Loaders.Select(t=>t.Name+"="+t.State+":"+t.Error)??[])+"; log="+string.Join(";",diagnostics));Thread.Sleep(3); }
            Native.Call(typeof(FileTaskScope),"DrainPostedUi",null,deadline);
        }
        private static string Decode(IEnumerable<string> lines,string key) {
            string line=lines.Single(v=>v.StartsWith(key+"=",StringComparison.Ordinal));return Encoding.UTF8.GetString(Convert.FromBase64String(line[(key.Length+1)..]));
        }
    }
    private sealed class NoCrashUi : ILegacyCrashAnalyzer { public void Collect(string version,IList<string>? log=null){} public bool Prepare()=>false; public void Analyze(){} public void Output(bool manual,List<string>? extra=null){} }
    private static string Tool(string name){string? path=Environment.GetEnvironmentVariable(name);if(path is null||!Path.IsPathFullyQualified(path)||!File.Exists(path))throw new IOException("Explicit installed Java prerequisite missing: "+name);return path;}
    private static void Add(ZipArchive zip,string path,byte[] value){using var output=zip.CreateEntry(path).Open();output.Write(value);}
    private static T ReadRecord<T>(string path){byte[] bytes=File.ReadAllBytes(path);int first=Array.IndexOf(bytes,(byte)'\n'),second=Array.IndexOf(bytes,(byte)'\n',first+1);Native.Check(first>0&&second>first,"Owned session record header invalid.");byte[] content=bytes[(second+1)..];Native.Check(Encoding.ASCII.GetString(bytes[(first+1)..second])==Convert.ToHexStringLower(SHA256.HashData(content)),"Owned session record digest invalid.");return System.Text.Json.JsonSerializer.Deserialize<T>(content)!;}
    private static string MainHash()=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Native.Main.Location)));
    private static void RunChild(string group) {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        string root = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Program.ValidateRoot(root);
        Process? child = null; Task<string>? stdout = null, stderr = null; bool stopped = false, success = false;
        try {
            string output = Path.Combine(root, "output");
            Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
            var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
            };
            foreach (string arg in new[] { "--groups", group, ChildSwitch, "--isolated", "--fixture", root }) start.ArgumentList.Add(arg);
            child = Process.Start(start) ?? throw new IOException("Owned replay child did not start.");
            stdout = child.StandardOutput.ReadToEndAsync(); stderr = child.StandardError.ReadToEndAsync();
            string receipt = Path.Combine(root, Receipt); var timer = Stopwatch.StartNew();
            while (!File.Exists(receipt)) {
                if (File.Exists(receipt + ".failed")) throw new IOException(File.ReadAllText(receipt + ".failed"));
                if (child.WaitForExit(10)) throw new IOException("Replay child exited without receipt: " + child.ExitCode);
                if (timer.Elapsed > TimeSpan.FromSeconds(110)) throw new TimeoutException("MMC replay child deadline exceeded.");
            }
            Native.Check(File.ReadAllText(receipt) == MainHash(), "Replay receipt belongs to a different Main assembly.");
            child.Kill(true); stopped = child.WaitForExit(5000);
            Native.Check(stopped && Task.WaitAll([stdout, stderr], 5000), "Replay child/output did not drain.");
            Console.Write(stdout.Result); Native.Check(stderr.Result.Length == 0, stderr.Result); success = true;
        } catch (Exception error) {
            if (child is not null) { if (!child.HasExited) child.Kill(true); stopped = child.WaitForExit(5000); }
            if (stopped && stdout is not null && stderr is not null) Task.WaitAll([stdout, stderr], 5000);
            if (stdout?.IsCompletedSuccessfully == true) File.WriteAllText(Path.Combine(root, "stdout.log"), stdout.Result);
            if (stderr?.IsCompletedSuccessfully == true) File.WriteAllText(Path.Combine(root, "stderr.log"), stderr.Result);
            throw new IOException("Owned MMC replay failed; retained evidence: " + root + "\n" +
                (stderr?.IsCompletedSuccessfully == true ? stderr.Result : "Output is still pending."), error);
        } finally {
            if (child is not null) { if (!child.HasExited) { child.Kill(true); stopped = child.WaitForExit(5000); } child.Dispose(); }
            if (success && stopped) { Program.ValidateRoot(root); Directory.Delete(root, true); }
        }
    }
}
