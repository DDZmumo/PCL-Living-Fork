using System.Diagnostics;
using System.Reflection;
using PCLCS;

internal static partial class MmcSessionTests {
    private static MmcMutableRuntimeAuthority ShortAuthority(string fixtureRoot)=>(MmcMutableRuntimeAuthority)typeof(MmcMutableRuntimeAuthority)
        .GetMethod("ForOwnedFixture",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[fixtureRoot])!;
    private static string ShortFixtureRoot()=>Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private static string DeepSessionScope()=>Path.Combine(Path.GetTempPath(),"pcllf-mmc-session-"+Guid.NewGuid().ToString("N"),new string('d',70),new string('e',70));
    private static void DeleteShortFixture(string root){
        string full=Path.GetFullPath(root);if(Path.GetDirectoryName(full)!=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))||!Guid.TryParseExact(Path.GetFileName(full),"N",out _))throw new IOException("Unexpected short fixture cleanup.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
    private static void ShortMutableIsolation(){
        string scope=ShortFixtureRoot();try{
            var authority=ShortAuthority(scope);using Fixture fixture=new(DeepSessionScope(),Guid.NewGuid(),true);using var lease=fixture.Lease();Guid sid=Guid.NewGuid();
            var mutable=MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(fixture.Root),fixture.Id,sid,new string('b',64),authority);
            Equal(MmcMutableRuntimeLayout.LocalAppDataV2,mutable.RuntimeBinding.MutableLayout);Equal(true,mutable.ExtractDirectory.Length+1+MmcSessionRuntimeDirectories.WindowsNativeSuffixBudget<260);
            Equal(false,mutable.SessionRoot.StartsWith(fixture.Root,StringComparison.OrdinalIgnoreCase));
            string sentinel=Path.Combine(mutable.ExtractDirectory,"native.dll");File.WriteAllText(sentinel,"owned native bytes");
            Throws<IOException>(()=>MmcSessionRuntimeDirectories.AbandonUnlaunched(lease,sid,mutable.RuntimeBinding));Equal(true,File.Exists(sentinel));
            string marker=Path.Combine(mutable.SessionRoot,"owner.txt");string expected=File.ReadAllText(marker);File.WriteAllText(marker,"foreign owner");
            Throws<IOException>(()=>MmcSessionRuntimeDirectories.AbandonUnlaunched(lease,sid,mutable.RuntimeBinding,authority));Equal(true,File.Exists(sentinel));
            File.WriteAllText(marker,expected);MmcSessionRuntimeDirectories.AbandonUnlaunched(lease,sid,mutable.RuntimeBinding,authority);
            MmcSessionRuntimeDirectories.AbandonUnlaunched(lease,sid,mutable.RuntimeBinding,authority);Equal(false,Directory.Exists(mutable.SessionRoot));
            Throws<IOException>(()=>MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(fixture.Root),fixture.Id,sid,new string('b',64),authority));
        }finally{DeleteShortFixture(scope);}
    }
    private static void ShortMutableExit(){
        string scope=ShortFixtureRoot();try{
            var authority=ShortAuthority(scope);using Fixture fixture=new(DeepSessionScope(),Guid.NewGuid(),true);using var lease=fixture.Lease();FixturePin? pin=null;Guid sid=Guid.NewGuid();
            var mutable=MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(fixture.Root),fixture.Id,sid,new string('c',64),authority);
            File.WriteAllText(Path.Combine(mutable.ExtractDirectory,"native.dll"),"owned");
            string marker=Path.Combine(mutable.SessionRoot,"owner.txt");string expected=File.ReadAllText(marker);
            using (var session=MmcSessions.Launch(lease,fixture.Request()with{SessionId=sid,RuntimeBinding=mutable.RuntimeBinding,RuntimeAuthority=authority},()=>{},(_,image)=>pin=new(image,fixture.PinFile))) {
                fixture.WaitChild();Equal(mutable.RuntimeBinding,session.Receipt.RuntimeBinding);
                Equal(MmcSessionObservation.Unknown,MmcSessions.Reconcile(lease,sid,new UnknownProcess(),_=>throw new Exception("Unknown must retain its pin."),authority).Observation);Equal(true,Directory.Exists(mutable.SessionRoot));
                fixture.Stop();if(!session.Process.WaitForExit(10000))throw new IOException("Owned short-runtime child did not exit.");
                File.WriteAllText(marker,"changed owner");
                Throws<IOException>(()=>session.CompleteVerifiedExit(lease));Equal(true,File.Exists(fixture.PinFile));
            }
            Equal(MmcSessionObservation.Unknown,MmcSessions.Reconcile(lease,sid,new MmcSessionProcessInspector(),_=>pin!.CompleteVerifiedExit()).Observation);Equal(true,File.Exists(fixture.PinFile));
            File.WriteAllText(marker,expected);
            Equal(MmcSessionObservation.ProvenExited,MmcSessions.Reconcile(lease,sid,new MmcSessionProcessInspector(),_=>pin!.CompleteVerifiedExit(),authority).Observation);
            Equal(false,File.Exists(fixture.PinFile));Equal(false,Directory.Exists(mutable.SessionRoot));MmcSessions.AssertNoActive(lease);
        }finally{DeleteShortFixture(scope);}
    }
    private sealed class UnknownProcess:IMmcSessionProcessInspector{public MmcSessionObservation Inspect(MmcSessionReceipt receipt,MmcSessionProcessRegistration? registration)=>MmcSessionObservation.Unknown;}
    private static bool TryRunShortHostChild(string[]args,out int exitCode){
        exitCode=0;if(args.Length==0||args[0]!="--mmc-short-host-child")return false;
        try{
            if(args.Length!=4)throw new IOException("Invalid owned short host child.");using Fixture fixture=new(args[1],Guid.Parse(args[2]),false);var authority=ShortAuthority(args[3]);using var lease=fixture.Lease();Guid sid=Guid.NewGuid();
            var mutable=MmcSessionRuntimeDirectories.Prepare(MmcInstancePaths.Resolve(fixture.Root),fixture.Id,sid,new string('d',64),authority);
            using var session=MmcSessions.Launch(lease,fixture.Request()with{SessionId=sid,RuntimeBinding=mutable.RuntimeBinding,RuntimeAuthority=authority},()=>{},(_,image)=>new FixturePin(image,fixture.PinFile));
            File.WriteAllText(fixture.HostReady,sid.ToString("N")+"\n"+mutable.SessionRoot);lease.Dispose();Thread.Sleep(Timeout.Infinite);
        }catch(Exception error){Console.Error.WriteLine(error);exitCode=21;}return true;
    }
    private static void ShortMutableHostDeath(){
        string scope=ShortFixtureRoot();try{
            var authority=ShortAuthority(scope);using Fixture fixture=new(DeepSessionScope(),Guid.NewGuid(),true);
            var start=new ProcessStartInfo(AppHost()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"--mmc-short-host-child",fixture.Scope,fixture.Id.ToString("D"),scope})start.ArgumentList.Add(arg);
            using var host=Process.Start(start)??throw new IOException("Owned short-runtime host did not start.");var stdout=host.StandardOutput.ReadToEndAsync();var stderr=host.StandardError.ReadToEndAsync();
            try{
                WaitFile(fixture.HostReady,15000);fixture.WaitChild();string[]ready=File.ReadAllLines(fixture.HostReady);Guid sid=Guid.ParseExact(ready[0],"N");string mutable=ready[1];
                host.Kill();if(!host.WaitForExit(5000))throw new IOException("Owned host did not exit.");
                using var lease=fixture.Lease();Equal(MmcSessionObservation.MatchingRunning,MmcSessions.Reconcile(lease,sid,new MmcSessionProcessInspector((_,nonce)=>fixture.ChildNonce()==nonce),_=>throw new Exception("Live pin must remain."),authority).Observation);
                Equal(true,File.Exists(fixture.PinFile));Equal(true,Directory.Exists(mutable));Throws<IOException>(()=>MmcSessions.AssertNoActive(lease));
                fixture.StopAndWait();Equal(MmcSessionObservation.ProvenExited,MmcSessions.Reconcile(lease,sid,new MmcSessionProcessInspector(),_=>File.Delete(fixture.PinFile),authority).Observation);
                Equal(false,File.Exists(fixture.PinFile));Equal(false,Directory.Exists(mutable));
            }finally{if(!host.HasExited){host.Kill(true);host.WaitForExit(5000);}}
        }finally{DeleteShortFixture(scope);}
    }
}
