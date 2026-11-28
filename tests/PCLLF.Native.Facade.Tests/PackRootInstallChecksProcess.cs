using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace PCLLF.Native.Facade.Tests;

internal static partial class PackRootInstallChecks
{
    private const string ChildSwitch="--pack-root-install-child", ReceiptName="pack-root-install-complete.json", Kind="PCLLF.HmclRootInstallWithRetainedOriginalManagers";
    internal static readonly string[] ManagerNames=["NetManager ThreadStarter 0","NetManager ThreadStarter 1","NetManager StatRefresher"];
    private static string MainHash()=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Native.Main.Location))).ToLowerInvariant();
    private sealed record Manager(string Name,int Id,bool Alive,bool Background);
    private sealed record Receipt(string Kind,string Nonce,int Pid,string Root,string MainHash,Guid MainMvid,string[] Checks,Manager[] Managers,int StoppedNetworkThreads,int StoppedTransfers);
    private static void VerifyChild(string[] args,FixtureEnvironment environment)
    {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        Native.Check(args.Length==7&&args[0]=="--groups"&&args[1]==Group&&args[2]==ChildSwitch&&args[4]=="--isolated"&&args[5]=="--fixture"&&System.Text.RegularExpressions.Regex.IsMatch(args[3],"^[a-f0-9]{32}$"),"Invalid HMCL dedicated child arguments.");
        Native.Check(Native.Full(args[6])==Native.Full(environment.Root),"HMCL child environment/owned root mismatch.");
    }
    private static void CompleteOwnedChild(FixtureEnvironment environment,string nonce,PackRootInstallChecksScope scope)
    {
        FileTaskShell.CloseUnshownHosts();
        Native.Check(Application.Current.Windows.Count==0,"HMCL root child left a real window open.");
        Thread[] threads=scope.NetworkThreads.ToArray(), managers=threads.Where(thread=>ManagerNames.Contains(thread.Name,StringComparer.Ordinal)).ToArray();
        Native.Check(managers.Length==3&&managers.Select(thread=>thread.Name).Distinct(StringComparer.Ordinal).Count()==3&&managers.All(thread=>thread.IsAlive&&!thread.IsBackground)&&threads.Except(managers).All(thread=>!thread.IsAlive),"HMCL completion requires exactly three original perpetual managers and all transient network threads returned.");
        Receipt result=new(Kind,nonce,Environment.ProcessId,Native.Full(environment.Root),MainHash(),Native.Main.ManifestModule.ModuleVersionId,Required,managers.Select(thread=>new Manager(thread.Name!,thread.ManagedThreadId,thread.IsAlive,thread.IsBackground)).ToArray(),threads.Length-managers.Length,scope.StoppedTransfers);
        string temporary=Path.Combine(environment.Root,ReceiptName+".pending");using(FileStream output=new(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(output,result);output.Flush(true);}
        Console.WriteLine("DIAG HMCL original pack/game/cleanup assertions and IO/listener barriers complete; retaining exactly three genuine LF foreground manager loops with all platform/settings guards.");Console.Out.Flush();Console.Error.Flush();File.Move(temporary,Path.Combine(environment.Root,ReceiptName));
        using ManualResetEventSlim processLifetime=new(false);processLifetime.Wait();throw new InvalidOperationException("Owned HMCL process lifetime unexpectedly returned.");
    }
    private static void RequireReceipt(string file,string nonce,int pid,string root)
    {
        FileInfo info=new(file);Native.Check(info.Length is >0 and <=65536&&(info.Attributes&FileAttributes.ReparsePoint)==0,"Invalid HMCL completion receipt.");
        using FileStream input=new(file,FileMode.Open,FileAccess.Read,FileShare.Read);Receipt result=JsonSerializer.Deserialize<Receipt>(input)??throw new InvalidDataException("Empty HMCL receipt.");
        Native.Check(result.Kind==Kind&&result.Nonce==nonce&&result.Pid==pid&&result.Root==Native.Full(root)&&result.MainHash==MainHash()&&result.MainMvid==Native.Main.ManifestModule.ModuleVersionId&&result.Checks.SequenceEqual(Required),"HMCL receipt must bind this exact nonce/PID/root/Main and every required assertion.");
        Native.Check(result.StoppedNetworkThreads>0&&result.StoppedTransfers>0&&result.Managers.Length==3&&result.Managers.Select(manager=>manager.Name).Order(StringComparer.Ordinal).SequenceEqual(ManagerNames.Order(StringComparer.Ordinal))&&result.Managers.All(manager=>manager.Id>0&&manager.Alive&&!manager.Background)&&result.Managers.Select(manager=>manager.Id).Distinct().Count()==3,"HMCL receipt lacks actual stopped network work and original retained manager identities.");
    }
    private static void RunOwnedChild()
    {
        ModpackUpdateCleanup.VerifyIsolatedChild();
        string root=Path.Combine(Path.GetTempPath(),"PclNativeFacade-"+Guid.NewGuid().ToString("N")),nonce=Guid.NewGuid().ToString("N");Directory.CreateDirectory(root);Program.ValidateRoot(root);
        Process? child=null;Task<string>? stdout=null,stderr=null;bool complete=false;
        Exception? primaryFailure=null;
        try{
            string output=Path.Combine(root,"output");Native.Call(typeof(Program),"CopyOutput",null,AppContext.BaseDirectory,output);
            string executable=Path.Combine(output,Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location)+".exe");Native.Check(File.Exists(executable),"Actual Windows fixture apphost is missing.");
            ProcessStartInfo start=new(executable){UseShellExecute=false,WorkingDirectory=root,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"--groups",Group,ChildSwitch,nonce,"--isolated","--fixture",root})start.ArgumentList.Add(argument);
            child=Process.Start(start)??throw new InvalidOperationException("Owned HMCL child failed to start.");stdout=child.StandardOutput.ReadToEndAsync();ObserveFault(stdout);stderr=child.StandardError.ReadToEndAsync();ObserveFault(stderr);Stopwatch watch=Stopwatch.StartNew();int Remaining()=>Math.Max(0,45000-checked((int)watch.ElapsedMilliseconds));
            string receipt=Path.Combine(root,ReceiptName);
            while(!File.Exists(receipt)){if(Remaining()==0)throw new TimeoutException("Owned HMCL chain did not produce a complete receipt within45s.");if(child.WaitForExit(Math.Min(25,Remaining())))throw new InvalidOperationException("Owned HMCL child exited without its complete receipt: "+child.ExitCode);}
            RequireReceipt(receipt,nonce,child.Id,root);Native.Check(!child.HasExited,"Owned HMCL child exited before parent assumed manager lifetime.");
            child.Kill(true);Native.Check(child.WaitForExit(Remaining())&&Task.WaitAll([stdout,stderr],Remaining())&&watch.ElapsedMilliseconds<45000,"Owned HMCL termination/output exceeded its same45s budget.");Native.Check(child.ExitCode==-1,"Owned Windows manager lifetime did not return expected termination -1.");
            Native.Check(stderr.GetAwaiter().GetResult().Length==0,"Owned HMCL child reported stderr despite its completion receipt.");Console.Write(stdout.GetAwaiter().GetResult());complete=true;
            Console.WriteLine("PASS pack-root-install: genuine HMCL root/game install, original cancel/failure cleanup and complete real IO; bound receipt followed by parent-owned -1 manager lifetime, not natural exit0.");
        }
        catch(Exception failure){primaryFailure=failure;throw;}
        finally
        {
            // Same bounded cleanup principles as Program.RunChild. Each owned
            // resource is attempted independently; a reader/kill failure cannot
            // skip the other pipe, exit observation or process-handle disposal.
            List<Exception> cleanupErrors=new();
            void Cleanup(string phase,Action action)
            {
                try{action();}
                catch(Exception failure){cleanupErrors.Add(new InvalidOperationException("HMCL child cleanup: "+phase,failure));}
            }
            bool canDelete=child is null;
            if(child is not null)
            {
                Stopwatch cleanup=Stopwatch.StartNew();
                int Remaining()=>Math.Max(0,5000-checked((int)cleanup.ElapsedMilliseconds));
                bool stopped=false,settled=false;
                Task[] drains=new Task?[]{stdout,stderr}.OfType<Task>().ToArray();
                void ObserveDrains(int milliseconds)
                {
                    try{Task.WaitAll(drains,milliseconds);}
                    catch(AggregateException){/* Record individual read faults after attempting both closes. */}
                    catch(Exception failure){cleanupErrors.Add(new InvalidOperationException("HMCL child output observation failed.",failure));}
                }
                try
                {
                    Cleanup("terminate only owned process",()=>{if(!child.HasExited)child.Kill(true);});
                    Cleanup("observe owned process exit",()=>stopped=child.WaitForExit(Remaining()));
                    // EOF gets only a bounded part of the SAME five seconds.
                    // A descendant retaining a pipe cannot justify waiting later
                    // on an incomplete ReadToEndAsync task without a deadline.
                    if(stopped)ObserveDrains(Math.Min(250,Remaining()));
                    try{Cleanup("close stdout reader",()=>child.StandardOutput.Dispose());}
                    finally{Cleanup("close stderr reader",()=>child.StandardError.Dispose());}
                    ObserveDrains(Remaining());
                    settled=stdout is not null&&stderr is not null&&drains.All(drain=>drain.IsCompleted);
                    foreach(Task drain in drains)
                    {
                        if(drain.IsFaulted)cleanupErrors.Add(new InvalidOperationException("HMCL child output read failed.",drain.Exception));
                        else if(drain.IsCanceled)cleanupErrors.Add(new TaskCanceledException("HMCL child output read was canceled.",null,default));
                    }
                    if(!stopped)cleanupErrors.Add(new InvalidOperationException("Owned HMCL child exit is unproven; retain its GUID fixture and live guards."));
                    if(!settled)cleanupErrors.Add(new TimeoutException("Owned HMCL child stdout/stderr did not both settle; retain its GUID fixture."));
                    if(cleanup.ElapsedMilliseconds>=5000)cleanupErrors.Add(new TimeoutException("HMCL child cleanup exceeded the same five-second deadline."));
                    if(!complete)
                    {
                        Cleanup("write failure metadata",()=>Console.Error.WriteLine("HMCL child failure evidence: PID="+child.Id+"; stopped="+stopped+"; settled="+settled+"; stdout="+stdout?.Status+"; stderr="+stderr?.Status));
                        Cleanup("write completed stdout tail",()=>PrintTail(stdout,"stdout"));
                        Cleanup("write completed stderr tail",()=>PrintTail(stderr,"stderr"));
                    }
                }
                finally{Cleanup("dispose owned process handle",child.Dispose);}
                canDelete=stopped&&settled&&cleanupErrors.Count==0;
            }
            if(canDelete)Cleanup("validate and remove stopped fixture",()=>{Program.ValidateRoot(root);if(Directory.Exists(root))Directory.Delete(root,true);});
            if(!canDelete||cleanupErrors.Count>0)Cleanup("report retained fixture",()=>Console.Error.WriteLine("HMCL child fixture retained after incomplete/failed cleanup: "+root));
            if(cleanupErrors.Count>0)
            {
                if(primaryFailure is null)throw new AggregateException("HMCL child cleanup failed; no successful acceptance is reported.",cleanupErrors);
                // The original exception is already rethrowing with its stack.
                // Cleanup diagnostics must not replace that business failure.
                try{Console.Error.WriteLine("HMCL cleanup faults after primary "+primaryFailure.GetType().Name+": "+string.Join(" | ",cleanupErrors.Select(failure=>failure.ToString())));}
                catch(Exception diagnosticFailure){cleanupErrors.Add(diagnosticFailure);}
            }
        }
        static void ObserveFault(Task task)=>_=task.ContinueWith(done=>{_=done.Exception;},CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        static void PrintTail(Task<string>? task,string channel)
        {
            // This read is legal only AFTER success: never await an incomplete,
            // canceled or faulted task in the failure/cleanup path.
            if(task?.IsCompletedSuccessfully!=true)return;
            string text=task.GetAwaiter().GetResult();
            bool truncated=text.Length>65536;int first=Math.Max(0,text.Length-65536);
            if(first>0&&char.IsLowSurrogate(text[first]))first++;
            byte[] bytes=System.Text.Encoding.UTF8.GetBytes(text.AsSpan(first).ToString());int start=Math.Max(0,bytes.Length-65536);
            if(start>0){truncated=true;while(start<bytes.Length&&(bytes[start]&0xc0)==0x80)start++;}
            string tail=System.Text.Encoding.UTF8.GetString(bytes.AsSpan(start));
            Console.Error.WriteLine("HMCL child "+channel+" tail:\n"+(truncated?"<truncated to final65536 UTF-8 bytes>\n":"")+tail);
        }
    }
}
