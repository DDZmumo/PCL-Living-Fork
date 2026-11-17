using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCLCS;
using PCLLF.MMCC.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class MmcInstanceLifecycleTests {
    internal static IEnumerable<(string Name,Action Run)> Cases() {
        yield return ("MMC path handoff preserves queued preferences and detaches every old provider without recreating its path", PreferenceHandoff);
        yield return ("MMC lifecycle permits require an exact published source and bound durable intent", JournalPermit);
        yield return ("MMC rename and same-volume move preserve identity/worlds and advance name projections without an installed Java", Relocation);
        yield return ("MMC lifecycle predecision cancellation rolls back while decided target conflicts recover forward", CancellationAndConflict);
        yield return ("MMC delete quarantines only private data and restores without reviving detached providers", DeleteRestore);
        yield return ("MMC exclusive primary file locks preserve the source and allow safe relocation retry", LockedDirectory);
        yield return ("MMC killed lifecycle producers recover exact predecision rollback and postdecision forward publication", KilledProducer);
        yield return ("MMC terminal recovery rejects another valid instance primary without attributing or changing it", TerminalReplacement);
        yield return ("MMC lists hide only exactly completed owned quarantines while unknown or damaged names stay visible", QuarantineVisibility);
    }
    private static void QuarantineVisibility() {
        using Fixture fixture = new(); var imported = fixture.Import();
        string versions = Path.GetDirectoryName(fixture.Source)!;
        string foreign = Path.Combine(versions, ".pcllf-mmc-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "unknown user data");
        Equal(false, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(foreign));
        var service = new MmcInstanceLifecycleBackend();
        var deleted = service.DeleteAsync(new(fixture.Source, imported.Definition!.InstanceId, 1,
            MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256)).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.Succeeded, deleted.Status);
        string root = deleted.CurrentRoot!;
        Equal(true, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(root));
        string marker = Path.Combine(root, "PCL", "MmcLifecyclePending.json"); byte[] original = File.ReadAllBytes(marker);
        File.WriteAllText(marker, "{}"); Equal(false, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(root)); File.WriteAllBytes(marker, original);
        string pending = Path.Combine(root, "PCL", "MmcImportPending.json"); File.WriteAllText(pending, "{}");
        Equal(false, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(root)); File.Delete(pending);
        string primary = Path.Combine(root, "PCL", "InstanceDefinition.json"); byte[] primaryBytes = File.ReadAllBytes(primary);
        File.AppendAllText(primary, " "); Equal(false, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(root)); File.WriteAllBytes(primary, primaryBytes);
        var restored = service.RestoreDeletedAsync(deleted.OperationDirectory!).GetAwaiter().GetResult();
        Equal(MmcInstanceBackendStatus.Succeeded, restored.Status); Equal(false, MmcInstanceLifecycleVisibility.IsCompletedQuarantine(fixture.Source));
        Equal("unknown user data", File.ReadAllText(Path.Combine(foreign, "keep.txt")));
    }
    internal static bool TryRunChild(string[] args,out int exitCode){
        exitCode=0;if(args.Length==0||args[0]!="--mmc-lifecycle-child")return false;
        try{
            if(args.Length!=7)throw new IOException("Bad owned lifecycle child arguments.");
            string scope=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(args[1]))!)!)!;
            string leaf=Path.GetFileName(scope);if(!leaf.StartsWith("pcllf-mmc-lifecycle-",StringComparison.Ordinal)||!Guid.TryParseExact(leaf[20..],"N",out _)||Path.GetDirectoryName(args[6])!=scope)throw new IOException("Lifecycle child is outside its GUID fixture.");
            var request=new MmcInstanceRelocationRequest(args[1],args[2],Guid.Parse(args[3]),1,args[4]){Checkpoint=(phase,directory)=>{
                if(args[5]=="prepared"&&phase==MmcLifecycleCheckpoint.StructurePrepared||args[5]=="decided"&&phase==MmcLifecycleCheckpoint.CommitDecided){File.WriteAllText(args[6],directory);using ManualResetEventSlim hold=new();hold.Wait();}
            }};
            var result=new MmcInstanceLifecycleBackend().RelocateAsync(request).GetAwaiter().GetResult();Console.Error.WriteLine(result.Failure);exitCode=93;
        }catch(Exception error){Console.Error.WriteLine(error);exitCode=94;}return true;
    }
    private static void LockedDirectory(){
        using Fixture fixture=new();var source=fixture.Import();var service=new MmcInstanceLifecycleBackend();MmcInstanceLifecycleResult result;
        string hash=MmcSavedSourceStore.ReadPrimary(source.Paths!).Sha256;
        using(var handle=new FileStream(Path.Combine(fixture.Source,"PCL","InstanceDefinition.json"),FileMode.Open,FileAccess.Read,FileShare.None)){
            result=service.RelocateAsync(new(fixture.Source,fixture.Target,source.Definition!.InstanceId,1,hash)).GetAwaiter().GetResult();
            Equal(MmcInstanceBackendStatus.Rejected,result.Status);Equal(true,Directory.Exists(fixture.Source));Equal(false,Directory.Exists(fixture.Target));
        }
        Equal(1L,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(fixture.Source)).Definition.Generation);
        var recovered=service.RelocateAsync(new(fixture.Source,fixture.Target,source.Definition!.InstanceId,1,hash)).GetAwaiter().GetResult();if(recovered.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(recovered.Failure);
        Equal(false,Directory.Exists(fixture.Source));Equal("world bytes",File.ReadAllText(Path.Combine(fixture.Target,"minecraft","saves","world","level.dat")));
    }
    private static void TerminalReplacement(){
        using Fixture fixture=new();using Fixture foreign=new();var source=fixture.Import();var other=foreign.Import();var service=new MmcInstanceLifecycleBackend();
        var result=service.RelocateAsync(new(fixture.Source,fixture.Target,source.Definition!.InstanceId,1,MmcSavedSourceStore.ReadPrimary(source.Paths!).Sha256)).GetAwaiter().GetResult();if(result.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(result.Failure);
        byte[] replacement=File.ReadAllBytes(Path.Combine(foreign.Source,"PCL","InstanceDefinition.json"));string targetPrimary=Path.Combine(fixture.Target,"PCL","InstanceDefinition.json");File.WriteAllBytes(targetPrimary,replacement);
        var refused=service.RecoverAsync(result.OperationDirectory!).GetAwaiter().GetResult();Equal(MmcInstanceBackendStatus.NeedsRecovery,refused.Status);Equal(true,refused.Definition is null);Equal(true,replacement.SequenceEqual(File.ReadAllBytes(targetPrimary)));Equal(other.Definition!.InstanceId,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(fixture.Target)).Definition.InstanceId);
    }
    private static void KilledProducer(){
        foreach(string phase in new[]{"prepared","decided"}){
            using Fixture fixture=new();var source=fixture.Import();string ready=Path.Combine(fixture.Root,"ready.txt");
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string value in new[]{"--mmc-lifecycle-child",fixture.Source,fixture.Target,source.Definition!.InstanceId.ToString("D"),MmcSavedSourceStore.ReadPrimary(source.Paths!).Sha256,phase,ready})start.ArgumentList.Add(value);
            using var child=Process.Start(start)??throw new IOException("Owned lifecycle child did not start.");var output=child.StandardOutput.ReadToEndAsync();var errors=child.StandardError.ReadToEndAsync();
            try{
                var timer=Stopwatch.StartNew();while(!File.Exists(ready)){if(child.HasExited)throw new IOException("Lifecycle child exited: "+errors.GetAwaiter().GetResult());if(timer.ElapsedMilliseconds>20000)throw new TimeoutException("Lifecycle child did not reach its exact boundary.");Thread.Sleep(2);}
                string directory=File.ReadAllText(ready);
                Equal(MmcInstanceBackendStatus.NeedsRecovery,new MmcInstanceLifecycleBackend().RecoverAsync(directory).GetAwaiter().GetResult().Status);
                child.Kill(true);if(!child.WaitForExit(5000))throw new IOException("Owned lifecycle child did not exit.");
                var recovered=new MmcInstanceLifecycleBackend().RecoverAsync(directory).GetAwaiter().GetResult();
                if(phase=="prepared"){if(recovered.Status!=MmcInstanceBackendStatus.Cancelled)throw new Exception(recovered.Failure);Equal(true,Directory.Exists(fixture.Source));Equal(false,Directory.Exists(fixture.Target));Equal(1L,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(fixture.Source)).Definition.Generation);}
                else{if(recovered.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(recovered.Failure);Equal(false,Directory.Exists(fixture.Source));Equal(2L,recovered.Definition!.Generation);}
                Equal("world bytes",File.ReadAllText(Path.Combine(recovered.CurrentRoot!,"minecraft","saves","world","level.dat")));
            }finally{if(!child.HasExited){child.Kill(true);child.WaitForExit(5000);}}
        }
    }
    private static void DeleteRestore(){
        using Fixture fixture=new();var source=fixture.Import();var service=new MmcInstanceLifecycleBackend();using CancellationTokenSource cancel=new();
        using var old=new InstanceConfigProvider(fixture.Source);
        string shared=Path.Combine(fixture.Root,"assets","unrelated.bin");Directory.CreateDirectory(Path.GetDirectoryName(shared)!);File.WriteAllText(shared,"shared bytes");
        var deleted=service.DeleteAsync(new(fixture.Source,source.Definition!.InstanceId,1,MmcSavedSourceStore.ReadPrimary(source.Paths!).Sha256){Checkpoint=(phase,_)=>{
            if(phase==MmcLifecycleCheckpoint.MarkerWritten){old.Set("queued-before-delete","keep pending value",false);MmcIniConfiguration.QueuePreferenceBatch(old,new Dictionary<string,string?>{["MmcJvmArgsMode"]="Custom",["VersionAdvanceJvm"]=""});}
            if(phase==MmcLifecycleCheckpoint.CommitDecided)cancel.Cancel();}},cancel.Token).GetAwaiter().GetResult();
        if(deleted.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(deleted.Failure);
        Equal(true,deleted.Quarantined);Equal(false,Directory.Exists(fixture.Source));Equal(true,cancel.IsCancellationRequested);Denied(old.Save);
        Equal("world bytes",File.ReadAllText(Path.Combine(deleted.CurrentRoot!,"minecraft","saves","world","level.dat")));Equal("shared bytes",File.ReadAllText(shared));
        Equal("keep pending value",Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(deleted.CurrentRoot!,"PCL","config.json"))).Value<string>("queued-before-delete"));
        Equal("",LegacyBaseOperations.ParseIni(File.ReadAllText(Path.Combine(deleted.CurrentRoot!,"PCL","Setup.ini")))["VersionAdvanceJvm"]);
        Equal(MmcManagementState.PendingTransaction,MmcManagementProbe.Read(deleted.CurrentRoot!).State);
        Equal(true,service.RecoverAsync(deleted.OperationDirectory!).GetAwaiter().GetResult().Quarantined);
        Directory.CreateDirectory(fixture.Source);File.WriteAllText(Path.Combine(fixture.Source,"foreign.bin"),"keep");
        Equal(MmcInstanceBackendStatus.NeedsRecovery,service.RestoreDeletedAsync(deleted.OperationDirectory!).GetAwaiter().GetResult().Status);Equal("keep",File.ReadAllText(Path.Combine(fixture.Source,"foreign.bin")));
        File.Delete(Path.Combine(fixture.Source,"foreign.bin"));Directory.Delete(fixture.Source,false);
        var restored=service.RestoreDeletedAsync(deleted.OperationDirectory!).GetAwaiter().GetResult();if(restored.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(restored.Failure);
        Equal(false,restored.Quarantined);Equal(source.Definition.InstanceId,restored.Definition!.InstanceId);Equal(1L,restored.Definition.Generation);Equal(false,Directory.Exists(deleted.CurrentRoot!));Denied(old.Save);
        Equal(MmcManagementState.ManagedMmc,MmcManagementProbe.Read(fixture.Source).State);Equal("shared bytes",File.ReadAllText(shared));
        Equal(MmcInstanceBackendStatus.Succeeded,service.RecoverAsync(deleted.OperationDirectory!).GetAwaiter().GetResult().Status);
    }
    private static void Relocation(){
        using Fixture fixture=new();var imported=fixture.Import();var service=new MmcInstanceLifecycleBackend();
        using var old=new InstanceConfigProvider(fixture.Source);old.Set("absolute",fixture.Source+"/minecraft/saves/world",false);old.Save();
        string renamed=Path.Combine(Path.GetDirectoryName(fixture.Source)!,"renamed");
        var first=service.RelocateAsync(new(fixture.Source,renamed,imported.Definition!.InstanceId,1,MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256,MmcInstanceRelocationKind.Rename)).GetAwaiter().GetResult();
        if(first.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(first.Failure);
        Equal(imported.Definition.InstanceId,first.Definition!.InstanceId);Equal(2L,first.Definition.Generation);Equal(false,Directory.Exists(fixture.Source));
        Equal(true,File.Exists(Path.Combine(renamed,"renamed.json")));Equal(false,File.Exists(Path.Combine(renamed,"source.json")));Denied(old.Save);
        string moved=Path.Combine(Path.GetDirectoryName(fixture.Target)!,"renamed");
        var second=service.RelocateAsync(new(renamed,moved,first.Definition.InstanceId,2,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(renamed)).Sha256)).GetAwaiter().GetResult();
        if(second.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(second.Failure);
        Equal(3L,second.Definition!.Generation);Equal(imported.Definition.InstanceId,second.Definition.InstanceId);
        Equal("world bytes",File.ReadAllText(Path.Combine(moved,"minecraft","saves","world","level.dat")));
        using var current=new InstanceConfigProvider(moved);Equal(moved.Replace('\\','/')+"/minecraft/saves/world",current.Read("absolute","",false));
        Equal(MmcManagementState.ManagedMmc,MmcManagementProbe.Read(moved).State);
        Equal(MmcInstanceBackendStatus.Succeeded,service.RecoverAsync(second.OperationDirectory!).GetAwaiter().GetResult().Status);
        Equal(false,Directory.Exists(renamed));Equal(false,Directory.Exists(fixture.Source));
    }
    private static void CancellationAndConflict(){
        foreach(string mode in new[]{"before","at-decision","after","conflict"}){
            using Fixture fixture=new();var imported=fixture.Import();using CancellationTokenSource cancel=new();var service=new MmcInstanceLifecycleBackend();
            var result=service.RelocateAsync(new(fixture.Source,fixture.Target,imported.Definition!.InstanceId,1,MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256){
                Checkpoint=(phase,_)=>{if(mode=="before"&&phase==MmcLifecycleCheckpoint.StructurePrepared)cancel.Cancel();
                    if(mode=="at-decision"&&phase==MmcLifecycleCheckpoint.BeforeDecision)cancel.Cancel();
                    if(mode=="after"&&phase==MmcLifecycleCheckpoint.CommitDecided)cancel.Cancel();
                    if(mode=="conflict"&&phase==MmcLifecycleCheckpoint.BeforeDirectoryMove){Directory.CreateDirectory(fixture.Target);File.WriteAllText(Path.Combine(fixture.Target,"foreign.txt"),"keep");}}
            },cancel.Token).GetAwaiter().GetResult();
            if(mode is "before" or "at-decision"){Equal(MmcInstanceBackendStatus.Cancelled,result.Status);Equal(1L,MmcSavedSourceStore.ReadPrimary(MmcInstancePaths.Resolve(fixture.Source)).Definition.Generation);Equal(false,Directory.Exists(fixture.Target));Equal(MmcManagementState.ManagedMmc,MmcManagementProbe.Read(fixture.Source).State);}
            else if(mode=="after"){if(result.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(result.Failure);Equal(true,cancel.IsCancellationRequested);Equal(2L,result.Definition!.Generation);}
            else{Equal(MmcInstanceBackendStatus.NeedsRecovery,result.Status);Equal("keep",File.ReadAllText(Path.Combine(fixture.Target,"foreign.txt")));Equal(MmcManagementState.PendingTransaction,MmcManagementProbe.Read(fixture.Source).State);
                File.Delete(Path.Combine(fixture.Target,"foreign.txt"));Directory.Delete(fixture.Target,false);
                var recovered=service.RecoverAsync(result.OperationDirectory!).GetAwaiter().GetResult();if(recovered.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(recovered.Failure);Equal(false,Directory.Exists(fixture.Source));Equal(2L,recovered.Definition!.Generation);}
        }
    }
    private static void PreferenceHandoff() {
        using Fixture fixture=new(); var imported=fixture.Import(); Guid id=imported.Definition!.InstanceId;
        using var old=new InstanceConfigProvider(fixture.Source);
        var roots=new[]{fixture.Source,fixture.Target}.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var pauses=new List<MmcConfigurationQuiescence>();var leases=new List<MmcAdmissionLease>();
        try {
            foreach(var root in roots) pauses.Add(MmcConfigurationCoordinator.Quiesce(root));
            foreach(var root in roots) leases.Add(MmcAdmissionGate.Acquire(root,id,TimeSpan.Zero));
            old.Set("late-user-path",fixture.Source+"/minecraft/config/personal.cfg",false);
            MmcIniConfiguration.QueuePreferenceBatch(old,new Dictionary<string,string?>{["MmcJvmArgsMode"]="Custom",["VersionAdvanceJvm"]=""});
            string hash=MmcSavedSourceStore.ReadPrimary(imported.Paths!).Sha256;
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);Directory.Move(fixture.Source,fixture.Target);
            MmcConfigurationCoordinator.CompletePathRelocation(pauses[Array.IndexOf(roots,fixture.Source)],pauses[Array.IndexOf(roots,fixture.Target)],
                leases[Array.IndexOf(roots,fixture.Source)],leases[Array.IndexOf(roots,fixture.Target)],id,hash);
            Denied(()=>old.Set("late","must not return",false));Denied(old.Save);Denied(()=>old.Read("late","",false));
            Equal(false,Directory.Exists(fixture.Source));
        } finally {for(int index=leases.Count-1;index>=0;index--)leases[index].Dispose();for(int index=pauses.Count-1;index>=0;index--)pauses[index].Dispose();}
        using var current=new InstanceConfigProvider(fixture.Target);
        Equal(fixture.Target.Replace('\\','/')+"/minecraft/config/personal.cfg",current.Read("late-user-path","",false));
        var prefs=MmcIniConfiguration.CapturePreferences(current);Equal("Custom",prefs.Ini["MmcJvmArgsMode"]);Equal("",prefs.Ini["VersionAdvanceJvm"]);
        current.Save();Equal(false,Directory.Exists(fixture.Source));Equal("world bytes",File.ReadAllText(Path.Combine(fixture.Target,"minecraft","saves","world","level.dat")));
    }
    private static void JournalPermit() {
        using Fixture fixture=new();var imported=fixture.Import();
        using var lease=MmcAdmissionGate.Acquire(fixture.Source,imported.Definition!.InstanceId,TimeSpan.Zero);
        var primary=MmcSavedSourceStore.ReadPrimary(imported.Paths!);
        var intent=new MmcLifecycleIntent(1,Guid.NewGuid(),"Move",imported.Definition.InstanceId,fixture.Source,fixture.Target,1,2,primary.Sha256,new string('a',64),"minecraft");
        Denied(()=>MmcInstanceLifecycleJournal.Create(intent with{InstanceId=Guid.NewGuid()},lease));
        var journal=MmcInstanceLifecycleJournal.Create(intent,lease);journal.MarkPending(fixture.Source);
        Equal(MmcManagementState.PendingTransaction,MmcManagementProbe.Read(fixture.Source).State);
        Denied(()=>MmcFileTransactions.AssertNoPending(lease));
        using var permit=journal.Enter(lease);MmcFileTransactions.AssertNoPending(lease);
        string other=Path.Combine(fixture.Source,"PCL","MmcImportPending.json");File.WriteAllText(other,"{}");Denied(()=>MmcFileTransactions.AssertNoPending(lease));File.Delete(other);
        string intentFile=Path.Combine(journal.DirectoryPath,"intent.record");File.AppendAllText(intentFile,"damaged");Denied(()=>journal.Enter(lease));
    }
    private static void Denied(Action work){try{work();}catch(IOException){return;}throw new Exception("Expected exact ownership/context rejection.");}
    private static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}.");}
    private sealed class Fixture:IDisposable {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"pcllf-mmc-lifecycle-"+Guid.NewGuid().ToString("N"));
        internal string Source=>Path.Combine(Root,"first","versions","source");
        internal string Target=>Path.Combine(Root,"second","versions","source");
        private readonly HttpClient client=new(new NoNetwork());
        internal Fixture(){
            Directory.CreateDirectory(Root);byte[] index=Encoding.UTF8.GetBytes("{\"objects\":{}}");string hash=Convert.ToHexStringLower(SHA1.HashData(index));
            using var zip=ZipFile.Open(Path.Combine(Root,"pack.zip"),ZipArchiveMode.Create);
            Add("pack/mmc-pack.json","""{"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}""");
            Add("pack/patches/net.minecraft.json",$$"""{"formatVersion":1,"uid":"net.minecraft","version":"1.7.10","mainClass":"fixture.Game","mainJar":{"name":"fixture:game:1","MMC-hint":"local","MMC-filename":"main.jar"},"assetIndex":{"id":"fixture","url":"https://fixture.invalid/index.json","sha1":"{{hash}}","size":{{index.Length}}},"compatibleJavaMajors":[8]}""");
            Add("pack/libraries/main.jar","local runtime bytes");Add("pack/minecraft/saves/world/level.dat","world bytes");
            void Add(string name,string text){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(text);}
        }
        internal MmcInstanceBackendResult Import(){
            var backend=new MmcInstanceBackend<MmcArtifactDownloadSpec>(new(Path.Combine(Root,"metadata"),new Uri("https://metadata.invalid/"),client),
                new(Path.Combine(Root,"libs"),Path.Combine(Root,"assets"),Path.Combine(Root,"cache")),value=>value,
                async(files,token)=>{foreach(var file in files)await File.WriteAllTextAsync(file.DownloadPath,"{\"objects\":{}}",new UTF8Encoding(false),token);});
            var value=backend.ImportAsync(new(Path.Combine(Root,"pack.zip"),Source,"source",new("windows","x86_64",8))).GetAwaiter().GetResult();
            if(value.Status!=MmcInstanceBackendStatus.Succeeded)throw new Exception(value.Failure);return value;
        }
        public void Dispose(){client.Dispose();InstanceConfigProviders.FlushAndDisposeWithin(Root);if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
    private sealed class NoNetwork:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new IOException("Lifecycle fixture has no network.");}
}
