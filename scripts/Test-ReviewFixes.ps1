param([Parameter(Mandatory=$true)][string]$ExecutablePath,
      [ValidateSet('Lock','Transaction','Recovery','Redirects','Workflow','All')][string]$Check='All')
# Real loaders and synthetic files/HTTP handlers only; no game, credentials or external network.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Net.Http
$references=@($ExecutablePath,'System.dll','System.Core.dll','System.Net.Http.dll',
 [Windows.Application].Assembly.Location,[Windows.Media.Brushes].Assembly.Location,[Windows.Threading.DispatcherObject].Assembly.Location)
foreach($file in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){
 $path=Join-Path $root $file;[Reflection.Assembly]::LoadFrom($path)|Out-Null;$references+=$path
}
$facade=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8/Facades/netstandard.dll'
$references+=$facade
$asm=[Reflection.Assembly]::LoadFrom($ExecutablePath)
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue}
$settings['Identify'].ValueCache['']='review-regression-fixture'
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclReviewFixes-'+[Guid]::NewGuid().ToString('N'))
$source=@'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeloongCore;
using PCL;
using Newtonsoft.Json.Linq;
using TaskLoader = PCL.ModLoader.LoaderTask<string,string>;
using ComboLoader = PCL.ModLoader.LoaderCombo<string>;
public static class ReviewFixChecks {
 const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static int released, executions;
 static void Require(bool value,string message){if(!value)throw new Exception(message);}
 static Action GuardedWorkload(Assembly asm,ComboLoader combo,TaskLoader step,string folder){
  var helper=asm.GetType("PCL.LoaderFileOperation");
  if(helper==null)return ()=>combo.WaitForExit(folder);
  var method=helper.GetMethod("CreateWorkload",Flags);
  return (Action)method.Invoke(null,method.GetParameters().Length==2?new object[]{combo,folder}:new object[]{combo,step,folder});
 }
 public static void LockLifetime(Assembly asm,string folder,bool nested){
  Directory.CreateDirectory(folder);
  var entered=new ManualResetEventSlim(false);var stopped=new ManualResetEventSlim(false);
  var cleaned=new ManualResetEventSlim(false);released=0;executions=0;
  Action<CancellationToken> copy=token=>{
   Interlocked.Increment(ref executions);
   entered.Set();
   // Deliberately non-interruptible, like a native file copy. Finally is the rollback boundary.
   try {while(Volatile.Read(ref released)==0)Thread.SpinWait(10000);token.ThrowIfCancellationRequested();}
   finally {File.WriteAllText(Path.Combine(folder,"cleanup.txt"),"complete");cleaned.Set();}
  };
  var step=new TaskLoader("guarded file fixture", task=>{
   var token=task.CreateCancellationToken();
   if(!nested){copy(token);return;}
   var leaf=new ModLoader.LoaderTask<int,int>("nested copy",t=>copy(t.CreateCancellationToken()));
   var inner=new ComboLoader("nested preparation",new ModLoader.LoaderBase[]{new ModLoader.LoaderCombo<int>("hidden nested task",new ModLoader.LoaderBase[]{leaf})});
   leaf.Show=false;
   var helper=asm.GetType("PCL.LoaderFileOperation");var method=helper==null?null:helper.GetMethod("CreateWorkload",Flags);
   using(token.Register(inner.Cancel)){
    if(method!=null&&method.GetParameters().Length==2)((Action)method.Invoke(null,new object[]{inner,folder}))();
    else inner.WaitForExit(folder,c:token);
   }
  });
  var combo=new ComboLoader("file fixture",new ModLoader.LoaderBase[]{step});
  Action workload=GuardedWorkload(asm,combo,step,folder);
  var worker=new RedoableWorker(workload);
  var instance=new ModMinecraft.McInstance(folder);
  Require(ModMinecraft.McInstanceLock.TryAcquire(worker,instance.PathVersion),"Fixture lock unavailable");
  worker.Stopped+=()=>stopped.Set();
  try{
   worker.Start();Require(entered.Wait(5000),"Real loader did not enter the file operation");
   combo.Cancel();
   Require(!stopped.Wait(250),"Canceled loader released its worker before file cleanup finished");
   Require(ModMinecraft.McInstanceLock.IsLocked(instance),"Canceled file operation released the instance lock");
   Require(!cleaned.IsSet,"Fixture cleanup unexpectedly completed before release");
  }finally{
   Volatile.Write(ref released,1);
   Require(stopped.Wait(5000),"Canceled operation never stopped");
   Require(cleaned.Wait(5000),"Worker stopped before its file cleanup");
   entered.Dispose();stopped.Dispose();cleaned.Dispose();
  }
  Require(!ModMinecraft.McInstanceLock.IsLocked(instance),"Completed operation retained its instance lock");
  // A child queued before cancellation must never start file work after the lease has closed.
  step.Start(folder,true);
  var limit=DateTime.UtcNow.AddSeconds(5);
  while(step.State==ModBase.LoadState.Loading&&DateTime.UtcNow<limit)Thread.Sleep(10);
  Require(executions==1,"Late child restarted a completed file transaction");
 }
 public static void CanceledBeforeStart(Assembly asm,string folder){
  var step=new TaskLoader("pre-canceled fixture",t=>File.WriteAllText(Path.Combine(folder,"unexpected.txt"),"bad"));
  var combo=new ComboLoader("pre-canceled fixture",new ModLoader.LoaderBase[]{step});
  var helper=asm.GetType("PCL.LoaderFileOperation");Require(helper!=null,"File-operation completion guard missing");
  var workload=GuardedWorkload(asm,combo,step,folder);
  using(var stopped=new ManualResetEventSlim(false)){
   var worker=new RedoableWorker(workload);
   var instance=new ModMinecraft.McInstance(folder);
   Require(ModMinecraft.McInstanceLock.TryAcquire(worker,instance.PathVersion),"Pre-canceled fixture lock unavailable");
   worker.Stopped+=()=>stopped.Set();
   combo.Cancel();worker.Start();
   Require(stopped.Wait(5000),"Pre-canceled operation did not stop");
   Require(!File.Exists(Path.Combine(folder,"unexpected.txt")),"Pre-canceled operation was restarted");
   Require(!ModMinecraft.McInstanceLock.IsLocked(instance),"Pre-canceled operation retained its lock");
  }
 }
 public static void TransactionCancellation(Assembly asm,string folder){
  Directory.CreateDirectory(folder);var game=Path.Combine(folder,"game");var version=Path.Combine(folder,"version");
  Directory.CreateDirectory(game);Directory.CreateDirectory(version);
  var type=asm.GetType("PCL.ModpackUpdate");var digest=type.GetMethod("Digest",Flags);
  var operations=new List<Tuple<string,string,string>>();
  foreach(var name in new[]{"a.txt","b.txt"}){
   var target=Path.Combine(game,name);var source=Path.Combine(folder,"new-"+name);
   File.WriteAllText(target,"old-"+name);File.WriteAllText(source,"new-"+name);
   operations.Add(Tuple.Create(target,source,(string)digest.Invoke(null,new object[]{target,"sha1"})));
  }
  using(var canceled=new CancellationTokenSource()){
   bool rejected=false;
   Action<int> atCommit=index=>{if(index==1)canceled.Cancel();};
   try{type.GetMethod("ApplyTransaction",Flags).Invoke(null,new object[]{game,version,operations,canceled.Token,atCommit,null});}
   catch(TargetInvocationException e){if(e.InnerException is OperationCanceledException)rejected=true;else throw;}
   Require(rejected,"Transaction committed after cancellation during file replacement");
   foreach(var name in new[]{"a.txt","b.txt"})Require(File.ReadAllText(Path.Combine(game,name))=="old-"+name,"Canceled transaction did not roll back: "+name);
  }
 }
 sealed class ScriptedHandler:HttpMessageHandler {
  public List<HttpRequestMessage> Seen=new List<HttpRequestMessage>();
  public Func<HttpRequestMessage,int,HttpResponseMessage> Respond;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   token.ThrowIfCancellationRequested();
   var copy=new HttpRequestMessage(request.Method,request.RequestUri);
   foreach(var header in request.Headers)copy.Headers.TryAddWithoutValidation(header.Key,header.Value);
   if(request.Content!=null)copy.Content=new ByteArrayContent(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
   Seen.Add(copy);return Task.FromResult(Respond(request,Seen.Count));
  }
 }
 static HttpResponseMessage Redirect(string target,HttpStatusCode status=HttpStatusCode.Redirect){
  var response=new HttpResponseMessage(status);response.Headers.Location=new Uri(target,UriKind.RelativeOrAbsolute);return response;
 }
 public static void Redirects(Assembly asm){
  var type=asm.GetType("PCL.SafeRedirectHandler");Require(type!=null,"HTTP redirect policy does not protect credential headers");
  var downloadClient=(HttpClient)asm.GetType("PCL.ModNet").GetField("ThreadClient",Flags).GetValue(null);
  var handlerField=typeof(HttpMessageInvoker).GetFields(Flags).Single(f=>f.FieldType==typeof(HttpMessageHandler));
  var productionHandler=handlerField.GetValue(downloadClient) as DelegatingHandler;
  Require(productionHandler!=null&&type.IsInstanceOfType(productionHandler),"Download transport bypasses the safe redirect policy");
  Require(!((HttpClientHandler)productionHandler.InnerHandler).AllowAutoRedirect,"Download transport still follows redirects outside the policy");
  var transport=new ScriptedHandler();
  transport.Respond=(r,n)=>n==1?Redirect("/v1/next"):n==2?Redirect("https://cdn.example.invalid/file"):n==3?Redirect("https://api.curseforge.com/return"):new HttpResponseMessage(HttpStatusCode.OK);
  using(var client=new HttpClient((HttpMessageHandler)Activator.CreateInstance(type,Flags,null,new object[]{transport},null))){
   var request=new HttpRequestMessage(HttpMethod.Get,"https://api.curseforge.com/v1/start");
   request.Headers.Add("x-api-key","synthetic-review-key");request.Headers.Add("Authorization","Bearer synthetic");
   using(var response=client.SendAsync(request).GetAwaiter().GetResult())Require(response.StatusCode==HttpStatusCode.OK,"Redirect chain failed");
   Require(transport.Seen.Count==4,"Redirect chain not followed");
   Require(transport.Seen[1].Headers.Contains("x-api-key"),"Same API host lost its key");
   Require(!transport.Seen[1].Headers.Contains("Authorization"),"Redirect retained Authorization");
   Require(!transport.Seen[2].Headers.Contains("x-api-key"),"Cross-host redirect leaked API key");
   Require(!transport.Seen[3].Headers.Contains("x-api-key"),"An untrusted redirect restored a stripped credential");
  }
  foreach(var status in new[]{301,302,303,307,308}){
   var methods=new ScriptedHandler{Respond=(r,n)=>n==1?Redirect("/next",(HttpStatusCode)status):new HttpResponseMessage(HttpStatusCode.OK)};
   using(var client=new HttpClient((HttpMessageHandler)Activator.CreateInstance(type,Flags,null,new object[]{methods},null))){
    using(var response=client.PostAsync("https://example.invalid/start",new StringContent("fixture-body")).GetAwaiter().GetResult()){
     Require(methods.Seen[1].Method==(status==307||status==308?HttpMethod.Post:HttpMethod.Get),"Redirect method changed incorrectly");
     if(status==307||status==308)Require(methods.Seen[1].Content.ReadAsStringAsync().Result=="fixture-body","Redirect lost request body");
     else Require(methods.Seen[1].Content==null,"GET redirect retained POST body");
    }
   }
  }
  foreach(var target in new[]{"http://api.curseforge.com/v1/next","file:///C:/unexpected"}){
   var blocked=new ScriptedHandler{Respond=(r,n)=>Redirect(target)};
   using(var client=new HttpClient((HttpMessageHandler)Activator.CreateInstance(type,Flags,null,new object[]{blocked},null))){
    bool failed=false;try{client.GetAsync("https://api.curseforge.com/start").GetAwaiter().GetResult();}catch(HttpRequestException){failed=true;}
    Require(failed&&blocked.Seen.Count==1,"Unsafe redirect target was sent");
   }
  }
  var loop=new ScriptedHandler{Respond=(r,n)=>Redirect("/loop")};
  using(var client=new HttpClient((HttpMessageHandler)Activator.CreateInstance(type,Flags,null,new object[]{loop},null))){
   bool failed=false;try{client.GetAsync("https://example.invalid/loop").GetAwaiter().GetResult();}catch(HttpRequestException){failed=true;}
   Require(failed&&loop.Seen.Count<=11,"Redirect loop was not bounded");
  }
 }
}
'@
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null
 $sourcePath=Join-Path $fixture 'Checks.cs';Put $sourcePath $source
 $checksPath=Join-Path $fixture 'checks.dll'
 $compiler=Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
 $arguments=@('/nologo','/target:library',('/out:'+$checksPath))
 $arguments+=@($references|ForEach-Object {'/reference:'+$_})
 & $compiler @arguments $sourcePath
 if($LASTEXITCODE -ne 0){throw 'Review regression fixture compilation failed'}
 [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($checksPath))|Out-Null
 if($Check -in @('All','Lock')){
  [ReviewFixChecks]::LockLifetime($asm,(Join-Path $fixture 'locked-version'),$false)
  [ReviewFixChecks]::LockLifetime($asm,(Join-Path $fixture 'nested-version'),$true)
  [ReviewFixChecks]::CanceledBeforeStart($asm,(Join-Path $fixture 'pre-canceled'))
  Write-Output 'PASS: cancellation retains the instance lock through cleanup; pre-canceled/late children cannot write.'
 }
 if($Check -in @('All','Transaction')){[ReviewFixChecks]::TransactionCancellation($asm,(Join-Path $fixture 'transaction'));Write-Output 'PASS: cancellation between file replacements rolls back the complete transaction.'}
 if($Check -in @('All','Recovery')){
  $version=Join-Path $fixture 'game/versions/example';$json=Join-Path $version 'example.json'
  $old='{"id":"example","clientVersion":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}'
  $new=$old.Replace('1.20.1','1.21.1');Put $json $old;Put (Join-Path $version 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
  $update=$asm.GetType('PCL.ModpackUpdate');$digest=$update.GetMethod('Digest',$flags)
  $before=$digest.Invoke($null,@([string]$json,'sha1'));Put $json $new;$after=$digest.Invoke($null,@([string]$json,'sha1'))
  $instance=[PCL.ModMinecraft+McInstance]::new($version)
  foreach($entry in $settings.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$instance.PathVersion]=$entry.DefaultValue}
  $settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
  if($instance.Version.VanillaName -cne '1.21.1'){throw 'Fixture did not cache the new runtime'}
  $box=Join-Path $version 'PCL/PackUpdateBackups/interrupted';Put (Join-Path $box '0.bak') $old
  Put (Join-Path $box 'transaction.json') (@{complete=$false;gameRoot=$instance.PathIndie;files=@(@{target=$json;backup=(Join-Path $box '0.bak');before=$before;after=$after})}|ConvertTo-Json -Depth 8)
  $recover=$asm.GetType('PCL.ModpackUpdateFlow').GetMethod('RecoverInstance',$flags)
  if($recover){$refreshed=$recover.Invoke($null,@($instance.PSObject.BaseObject))}else{$update.GetMethod('Recover',$flags).Invoke($null,@([string]$instance.PathVersion,[string]$instance.PathIndie));$refreshed=$instance}
  if($refreshed.Version.VanillaName -cne '1.20.1'){throw 'Recovery retained the pre-rollback runtime cache; retry would skip runtime installation'}
  if($refreshed.Version.VanillaName -ceq '1.21.1'){throw 'Retry does not detect the required runtime update'}
  Write-Output 'PASS: interrupted runtime rollback refreshes the instance before update retry.'
 }
 if($Check -in @('All','Redirects')){[ReviewFixChecks]::Redirects($asm);Write-Output 'PASS: same-host/cross-host credentials, unsafe redirect targets and redirect loops.'}
 if($Check -in @('All','Workflow')){
  $workflow=[IO.File]::ReadAllText((Join-Path $root '.github/workflows/build.yml'))
  foreach($path in @('PCLCS/**','MeloongCore','.gitmodules','*.sln')){
   if(([regex]::Matches($workflow,[regex]::Escape("- '$path'"))).Count -ne 2){throw "CI push/PR filters omit build input: $path"}
  }
  Write-Output 'PASS: push and PR filters include C# projects, the submodule and solution inputs.'
 }
}finally{
 $app.Shutdown()
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclReviewFixes-[a-f0-9]{32}\z'){throw 'Unsafe review fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
