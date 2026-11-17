using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using MeloongCore;
using PCL;
using PCLCS;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLLF.Native.Facade.Tests;

/// <summary>Opt-in post-publication checks; no archive extraction, adoption, repair or game launch.</summary>
internal static class MmcGtnhPostPublishChecks {
    private const string Parent = "--mmc-gtnh-post-publish-check", Child = "--mmc-gtnh-post-publish-child", Receipt = "post-publish-result.json";
    private static FixtureEnvironment? retained;
    internal static bool TryRun(string[] args, out int result) {
        result = 0;
        if (!args.Contains(Parent, StringComparer.Ordinal) && !args.Contains(Child, StringComparer.Ordinal)) return false;
        try { result = args.Contains(Child, StringComparer.Ordinal) ? RunChild(args) : RunParent(args); }
        catch (Exception error) { Console.Error.WriteLine("FAIL owned GTNH post-publication check: " + error); result = 1; }
        return true;
    }
    internal static void InstallEmptyLegacyAccountStore() {
        // Same schema-only boundary as MmcManagedImportChecks: encrypted legacy account keys
        // bypass ValueCache. Never call the real user registry for these two empty owned values.
        object settings = Native.Get(Native.Field(typeof(Settings), "Backend").GetValue(null)!, "host")!;
        var guarded = (Func<string, string, string?, string?>)Native.Get(settings, "ReadRegistry")!;
        Native.Set(settings, "ReadRegistry", new Func<string, string, string?, string?>((path, key, fallback) =>
            path == @"HKEY_CURRENT_USER\Software\PCL" && key is "CacheNideServer" or "CacheAuthServerServer" ? null : guarded(path, key, fallback)));
    }
    internal static void DrainUi() {
        while (true) {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher; DispatcherFrame frame = new(); bool idle = false;
            DispatcherTimer timer = new(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            EventHandler tick = (_, _) => frame.Continue = false; timer.Tick += tick;
            var operation = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { idle = true; frame.Continue = false; }));
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); timer.Tick -= tick; if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort(); }
            if (idle) return;
        }
    }
    private static int RunParent(string[] args) {
        string id = Option(args, "--gtnh-case") ?? throw new ArgumentException("Post-check needs --gtnh-case java8 or java21.");
        if (id is not ("java8" or "java21")) throw new ArgumentException("Post-check accepts one completed java8/java21 case.");
        string run = Path.GetFullPath(Option(args, "--gtnh-owned-run") ?? throw new ArgumentException("Missing --gtnh-owned-run."));
        string original = MmcGtnhAcceptance.OwnedCacheCase(run, id, Path.Combine(MmcGtnhAcceptance.FindRepository(), "artifacts", "mmc-gtnh-acceptance"));
        using (var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(original, "import-result.json"))))
            Native.Check(source.RootElement.GetProperty("Status").GetString() == "Succeeded", "Post-check needs a completed actual import receipt.");
        string proof = Path.Combine(run, "post-publish-" + id + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(proof);
        string fixture = Path.Combine(Path.GetTempPath(), "PclNativeFacade-" + Guid.NewGuid().ToString("N")), nonce = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(fixture); Program.ValidateRoot(fixture);
        File.WriteAllText(Path.Combine(proof, "owner.json"), JsonSerializer.Serialize(new { Kind="PCLLF.GtnhPostPublish", Root=proof, OriginalCase=original, Fixture=fixture, Nonce=nonce, Keep=true }));
        string output = Path.Combine(fixture, "output"); Native.Call(typeof(Program), "CopyOutput", null, AppContext.BaseDirectory, output);
        var start = new ProcessStartInfo(Path.Combine(output, Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".exe")) { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=fixture, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (string arg in new[] {Child,"--proof-root",proof,"--case-root",original,"--nonce",nonce,"--isolated","--fixture",fixture}) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Owned post-check child did not start.");
        Task stdout = Pump(child.StandardOutput, Path.Combine(proof,"stdout.log"), false), stderr = Pump(child.StandardError,Path.Combine(proof,"stderr.log"),true);
        Console.WriteLine("RETAINED GTNH post-publication proof: " + proof);
        try {
            while (!File.Exists(Path.Combine(proof,Receipt))) if (child.WaitForExit(100)) throw new IOException("Post-check child exited without a receipt: " + child.ExitCode);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(proof,Receipt))); var r=receipt.RootElement;
            Native.Check(r.GetProperty("Nonce").GetString()==nonce && r.GetProperty("Pid").GetInt32()==child.Id && r.GetProperty("ProofRoot").GetString()==proof,"Post-check receipt owner mismatch.");
            Native.Check(r.GetProperty("Status").GetString()=="Succeeded", "Post-check failed: " + r.ToString());
            return 0;
        } finally {
            if (!child.HasExited) child.Kill(true);
            child.WaitForExit(); Task.WaitAll(stdout,stderr);
        }
    }
    private static int RunChild(string[] args) {
        string proof=Path.GetFullPath(Option(args,"--proof-root")!), original=Path.GetFullPath(Option(args,"--case-root")!), fixture=Path.GetFullPath(Option(args,"--fixture")!), nonce=Option(args,"--nonce")!;
        Program.ValidateRoot(fixture);
        Native.Check(args.Contains("--isolated",StringComparer.Ordinal) && Guid.TryParseExact(nonce,"N",out _) && Native.Full(AppContext.BaseDirectory)==Native.Full(Path.Combine(fixture,"output")),"Post-check child identity differs.");
        using var owner=JsonDocument.Parse(File.ReadAllText(Path.Combine(proof,"owner.json"))); var o=owner.RootElement;
        Native.Check(o.GetProperty("Kind").GetString()=="PCLLF.GtnhPostPublish" && o.GetProperty("Root").GetString()==proof && o.GetProperty("OriginalCase").GetString()==original && o.GetProperty("Fixture").GetString()==fixture && o.GetProperty("Nonce").GetString()==nonce,"Post-check marker mismatch.");
        string run=Directory.GetParent(original)!.FullName, id=Path.GetFileName(original);
        Native.Check(id is "java8" or "java21" && Native.Full(Directory.GetParent(proof)!.FullName)==Native.Full(run),"Post-check escaped its retained run.");
        _=MmcGtnhAcceptance.OwnedCacheCase(run,id,Directory.GetParent(run)!.FullName);
        retained=new(fixture); ModpackUpdateCleanup.VerifyIsolatedChild();
        object report;
        try {
            ModBase.RunInUi(); _=FileTaskShell.Install(); InstallEmptyLegacyAccountStore();
            ConfigProviders.Default=new JsonConfigProvider(Path.Combine(proof,"private-profile.json"));
            using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(original,"import-result.json"))); var r=receipt.RootElement;
            string instance=r.GetProperty("InstanceRoot").GetString()!, game=Path.Combine(original,"game");
            Native.Check(Native.Full(instance)==Native.Full(Path.Combine(game,"versions","GTNH-2.9.0-"+id)),"Published instance escaped its owned case.");
            string definition=Path.Combine(instance,"PCL","InstanceDefinition.json"), projection=Path.Combine(instance,Path.GetFileName(instance)+".json");
            string definitionBefore=Hash(definition), projectionBefore=Hash(projection);
            var inspection = MmcInstanceInspector.Read(instance);
            Native.Check(inspection.IsConsistent,"Published definition is inconsistent before refresh.");
            string actualMainSha = Hash(Native.Main.Location);
            Native.Check(actualMainSha == r.GetProperty("MainSha256").GetString(), "Post-check is not using the actual imported Main build.");
            var paths = MmcInstancePaths.Resolve(instance, inspection.Definition!.Layout.GameRootLeaf);
            var locks = inspection.Definition.ArtifactLocks.ToDictionary(item => item.ArtifactKey, StringComparer.Ordinal);
            int verifiedLibraries = 0, verifiedNatives = 0;
            foreach (var item in r.GetProperty("Libraries").EnumerateArray()) {
                var expected = locks[item.GetProperty("Key").GetString()!];
                Native.Check(expected.AdoptedSha256 == item.GetProperty("AdoptedSha256").GetString() && expected.TargetRelativePath == item.GetProperty("TargetRelativePath").GetString(), "Published input lock differs from its actual import receipt.");
                string path = paths.ResolveRelative(MmcPathRole.Instance, expected.TargetRelativePath);
                Native.Check(new FileInfo(path).Length == expected.SizeBytes && Hash(path) == expected.AdoptedSha256, "Published runtime input changed after import.");
                verifiedLibraries++;
            }
            foreach (var item in inspection.Definition.DerivedOutputs.Where(item => item.Kind == MmcDerivedOutputKind.NativeFile)) {
                string path = paths.ResolveRelative(MmcPathRole.Instance, item.TargetRelativePath);
                Native.Check(new FileInfo(path).Length == item.SizeBytes && Hash(path) == item.Sha256, "Published native differs from its parent-derived bytes.");
                verifiedNatives++;
            }
            Native.Field(typeof(ModMinecraft),"_McFolderSelected").SetValue(null,game+Path.DirectorySeparatorChar);
            ModMinecraft.McInstanceListForceRefresh=true; ModMinecraft.McInstanceListLoader.Start(game+Path.DirectorySeparatorChar,IsForceRestart:true);
            while (ModMinecraft.McInstanceListLoader.State is ModBase.LoadState.Waiting or ModBase.LoadState.Loading || ModMinecraft.McInstanceListLoader.LastRunningThread?.IsAlive==true) { DrainUi(); Thread.Sleep(2); }
            DrainUi();
            Native.Check(ModMinecraft.McInstanceListLoader.State==ModBase.LoadState.Finished,"Actual published list refresh failed: "+ModMinecraft.McInstanceListLoader.Error);
            Native.Check(Native.Field(typeof(ModMinecraft),"_McInstanceSelected").GetValue(null) is not null,"Actual refresh did not select the published instance.");
            Native.Check(MmcInstanceInspector.Read(instance).IsConsistent && Hash(definition)==definitionBefore && Hash(projection)==projectionBefore,"UI refresh modified or invalidated the managed definition/projection.");
            retained.AssertNoForbiddenIo();
            report=new {SchemaVersion=1,Status="Succeeded",Nonce=nonce,Pid=Environment.ProcessId,ProofRoot=proof,OriginalCase=original,InstanceRoot=instance,DefinitionSha256=definitionBefore,ProjectionSha256=projectionBefore,MainSha256=actualMainSha,VerifiedLibraryCount=verifiedLibraries,VerifiedNativeCount=verifiedNatives,UiRefreshDrained=true,NoForbiddenIo=true,Reimported=false,GameLaunchTested=false,RepairTested=false,Keep=true};
            Console.WriteLine("PASS actual retained GTNH published definition + list worker + dispatcher idle; zero forbidden reads/writes; no reimport/game.");
        } catch(Exception error) {report=new {SchemaVersion=1,Status="Failed",Nonce=nonce,Pid=Environment.ProcessId,ProofRoot=proof,Error=error.ToString(),Reimported=false,GameLaunchTested=false}; Console.Error.WriteLine(error);}
        string pending=Path.Combine(proof,Receipt+".pending");
        using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,report,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
        File.Move(pending,Path.Combine(proof,Receipt),false);
        Console.Out.Flush();Console.Error.Flush();using ManualResetEventSlim life=new();life.Wait();return 1;
    }
    private static string Hash(string path) { using var stream=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string? Option(string[] args,string name) {int n=Array.IndexOf(args,name);return n<0?null:n+1<args.Length?args[n+1]:throw new ArgumentException("Missing "+name);}
    private static async Task Pump(StreamReader reader,string path,bool error){await using var output=new StreamWriter(path,false);string? line;while((line=await reader.ReadLineAsync())is not null){await output.WriteLineAsync(line);await output.FlushAsync();(error?Console.Error:Console.Out).WriteLine(line);}}
}
