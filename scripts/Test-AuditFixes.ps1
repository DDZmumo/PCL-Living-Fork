param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# 只使用虚构账号、独立测试注册表和随机名称的互斥锁，不访问真实账号存储。
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')) { [Reflection.Assembly]::LoadFrom((Join-Path $root $path)) | Out-Null }
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$flags=[Reflection.BindingFlags]'NonPublic,Public,Static'
$settings=$asm.GetType('PCL.Settings',$true)
$entries=$settings.GetField('Entries').GetValue($null)
$encode=$settings.GetMethod('DesEncrypt',$flags)
$decode=$settings.GetMethod('DesDecrypt',$flags)
function New-Map { return ,([Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)) }
function Encode([string]$value) { return $encode.Invoke($null,@($value,'PCLsynthetic-device')) }
$plan=$settings.GetMethod('PlanLegacyMicrosoftUpgrade',$flags)
$raw=New-Map
$raw['CacheMsOAuthRefresh']=Encode 'old-refresh'
$raw['CacheMsV2OAuthRefresh']=Encode 'new-refresh'
$raw['CacheMsV2Expires']=Encode '9999999999'
$result=$plan.Invoke($null,@($raw,'synthetic-device'))
if($result.Count -ne 1 -or -not $result.ContainsKey('CacheMsV2Migrated')) { throw 'Existing V2 session was overwritten' }
$raw['CacheMsV2OAuthRefresh']=Encode ''
$raw['CacheMsV2Expires']=Encode '0'
$result=$plan.Invoke($null,@($raw,'synthetic-device'))
if($result.Count -ne 1) { throw 'Explicit logout was resurrected' }
$raw['CacheMsV2OAuthRefresh']='invalid-cipher'
$result=$plan.Invoke($null,@($raw,'synthetic-device'))
if($result.Count -ne 1) { throw 'Damaged but existing V2 data was overwritten' }
$raw.Remove('CacheMsV2OAuthRefresh') | Out-Null
$raw.Remove('CacheMsV2Expires') | Out-Null
$result=$plan.Invoke($null,@($raw,'synthetic-device'))
if($result.Count -ne 7 -or $decode.Invoke($null,@($result['CacheMsV2Expires'],'PCLsynthetic-device')) -ne '0') { throw 'Legacy upgrade group or expiry is incorrect' }
$raw['CacheMsOAuthRefresh']='invalid-cipher'
if($plan.Invoke($null,@($raw,'synthetic-device')).Count -ne 0) { throw 'Invalid old session was migrated' }
$route=$settings.GetMethod('RegistryFolderFor',$flags)
if($route.Invoke($null,@($entries['CacheMsV2Migrated'])) -ne 'PCL') { throw 'Migration flag is not shared' }
$entry=$entries['LoginMsJson']
$oldCache=$null
$hadCache=$entry.ValueCache.TryGetValue('',[ref]$oldCache)
$entry.ValueCache['']='{"stale":"cached-token"}'
$script:fakeRaw=Encode '{"first":"token-1"}'
$readerType=([Func[object,string]]).GetGenericTypeDefinition().MakeGenericType($asm.GetType('PCL.Settings+Setting'),[string])
$reader=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{ param($ignoredEntry) return $script:fakeRaw },$readerType)
$getCore=$settings.GetMethod('GetCore',$flags)
try {
 $first=$getCore.Invoke($null,@('LoginMsJson',$null,$reader,'synthetic-device'))
 $script:fakeRaw=Encode '{"second":"token-2"}'
 $second=$getCore.Invoke($null,@('LoginMsJson',$null,$reader,'synthetic-device'))
 if($first -ne '{"first":"token-1"}' -or $second -ne '{"second":"token-2"}') { throw 'Shared account reads returned stale process cache' }
} finally { $entry.ValueCache.Clear();if($hadCache){$entry.ValueCache['']=$oldCache};$script:fakeRaw=$null }
$testPath='Software\LivingPCL-AuditTests\'+[Guid]::NewGuid().ToString('N')
$legacy=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testPath+'\Legacy')
$official=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testPath+'\Official')
try {
 $sharedPath=$settings.GetMethod('RegistryFolderFor',$flags)
 foreach($key in @('LaunchFolders','ToolDownloadThread','ToolDownloadSpeed')) {
  if($sharedPath.Invoke($null,@($entries[$key])) -ne 'PCL') { throw "Compatible preference $key is not shared with PCL" }
 }
 if($settings.GetMethod('IsCompatiblePclSetting',$flags).Invoke($null,@($entries['ToolLauncherUpdateIgnoredTag']))) { throw 'Branch updater state would leak into PCL' }
 $migrate=$settings.GetMethod('MigrateSharedPclPreferences',$flags)
 $legacy.SetValue('LaunchFolders','Local>C:\Local\')
 $official.SetValue('LaunchFolders','Official>D:\Official\')
 $legacy.SetValue('ToolDownloadThread','12')
 $official.SetValue('ToolDownloadThread','16')
 $legacy.SetValue('ToolDownloadSpeed','8')
 $migrate.Invoke($null,@($legacy,$official)) | Out-Null
 if($official.GetValue('LaunchFolders') -ne 'Local>C:\Local\') { throw 'Locally removed game folders were restored instead of preserving LivingPCL selection' }
 if($official.GetValue('ToolDownloadThread') -ne '16') { throw 'Existing official generic preference was overwritten' }
 if($official.GetValue('ToolDownloadSpeed') -ne '8') { throw 'Previously saved LivingPCL preference was not migrated' }
 $legacy.SetValue('LaunchFolders','Other>E:\Other\')
 $migrate.Invoke($null,@($legacy,$official)) | Out-Null
 if($official.GetValue('LaunchFolders') -ne 'Local>C:\Local\') { throw 'Legacy migration reran and overwrote shared preferences' }
 $reset=$settings.GetMethod('ResetRegistryPreference',$flags)
 $reset.Invoke($null,@($official,$entries['LaunchFolders'])) | Out-Null
 if($official.GetValue('LaunchFolders') -ne '') { throw 'Reset game folders did not update the official shared registry' }
 $reset.Invoke($null,@($official,$entries['ToolDownloadThread'])) | Out-Null
 if([string]$official.GetValue('ToolDownloadThread') -ne [string]$entries['ToolDownloadThread'].DefaultValue) { throw 'Shared default preference reset was not persisted' }
 $reset.Invoke($null,@($official,$entries['ToolDownloadThread'])) | Out-Null
 if($null -eq $official.GetValue('ToolDownloadThread',$null)) { throw 'Repeated reset removed shared default override' }
 $legacy.DeleteValue('LivingPclSharedPreferencesMigrated',$false)
 $legacy.SetValue('LaunchFolders','malformed-relative-folder')
 $migrate.Invoke($null,@($legacy,$official)) | Out-Null
 if($official.GetValue('LaunchFolders') -ne '') { throw 'Corrupted local folder list overwrote valid official preference' }
} finally {
 $legacy.Dispose()
 $official.Dispose()
 if(-not $testPath.StartsWith('Software\LivingPCL-AuditTests\')) { throw 'Invalid test registry cleanup path' }
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testPath,$false)
}
$policy=$asm.GetType('PCL.LoginCertificatePolicy',$true).GetMethod('Accept',$flags)
foreach($errorCode in @(1,2,4,7)) {
 foreach($hostName in @('xboxlive.com','user.auth.xboxlive.com','api.minecraftservices.com','API.MINECRAFTSERVICES.COM.')) {
  if(-not $policy.Invoke($null,@($hostName,[Net.Security.SslPolicyErrors]$errorCode,$false))) { throw 'Login compatibility host rejected' }
  if($policy.Invoke($null,@($hostName,[Net.Security.SslPolicyErrors]$errorCode,$true))) { throw 'Strict login policy bypassed' }
 }
 foreach($sender in @($null,123,'github.com','api.github.com','xboxlive.attacker.test','notminecraftservices.com','api.minecraftservices.com.attacker.test')) {
  if($policy.Invoke($null,@($sender,[Net.Security.SslPolicyErrors]$errorCode,$false))) { throw 'Untrusted certificate sender was accepted' }
 }
}
if(-not $policy.Invoke($null,@($null,[Net.Security.SslPolicyErrors]0,$true))) { throw 'Valid certificate rejected' }
$updater=$asm.GetType('PCL.ModLauncherUpdate',$true).GetMethod('ShouldPromptForUpdate',$flags)
if(-not $updater.Invoke($null,@('v1.0.2','1.0.1',''))) { throw 'Update failure would suppress retry' }
if($updater.Invoke($null,@('v1.0.2','1.0.1','v1.0.2'))) { throw 'Explicitly ignored update is repeated' }
if(-not $updater.Invoke($null,@('v1.0.3','1.0.1','v1.0.2'))) { throw 'A later update was suppressed' }
$choice=$asm.GetType('PCL.ModLauncherUpdate').GetMethod('IgnoreTagAfterChoice',$flags)
if($choice.Invoke($null,@('v1.0.2','v1.0.2',1)) -ne '') { throw 'Manual retry did not revoke an earlier ignored update' }
if($choice.Invoke($null,@('','v1.0.2',2)) -ne 'v1.0.2') { throw 'Explicit update ignore was not retained' }
$cancel=[Func[bool]]{ return $true }
$empty=[Collections.Generic.Dictionary[string,object]]::new()
foreach($name in @('SaveMicrosoftSession','SaveAccountGroup')) {
 $stopped=$false
 try {
  if($name -eq 'SaveMicrosoftSession') { $settings.GetMethod($name).Invoke($null,@($empty,'synthetic-user',$cancel)) | Out-Null }
  else { $settings.GetMethod($name).Invoke($null,@($empty,$cancel)) | Out-Null }
 } catch { $stopped=$_.Exception.GetBaseException() -is [OperationCanceledException] }
 if(-not $stopped) { throw 'Canceled login could write a session' }
}
Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public static class LivingPclMutexAudit {
    public static void CheckQueuedCancellation(Assembly assembly) {
        Type settings=assembly.GetType("PCL.Settings");
        object sync=settings.GetField("SharedAccountSync",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        foreach(string name in new[]{"SaveMicrosoftSession","SaveAccountGroup"}) {
            bool canceled=false, observed=false;
            Exception failure=null;
            ManualResetEvent started=new ManualResetEvent(false), finished=new ManualResetEvent(false);
            Func<bool> check=()=>Volatile.Read(ref canceled);
            object values=new System.Collections.Generic.Dictionary<string,object>();
            Thread worker=new Thread(()=> {
                started.Set();
                try {
                    object[] args=name=="SaveMicrosoftSession" ? new object[]{values,"synthetic-user",check} : new object[]{values,check};
                    settings.GetMethod(name).Invoke(null,args);
                } catch(Exception e) { observed=e.GetBaseException() is OperationCanceledException; if(!observed) failure=e; }
                finally { finished.Set(); }
            });
            Monitor.Enter(sync);
            try {
                worker.IsBackground=true; worker.Start();
                if(!started.WaitOne(3000)) throw new Exception("Queued login test did not start");
                Volatile.Write(ref canceled,true);
            } finally { Monitor.Exit(sync); }
            if(!finished.WaitOne(3000)) throw new Exception("Queued canceled login did not finish");
            worker.Join(); started.Dispose(); finished.Dispose();
            if(failure!=null || !observed) throw new Exception("A queued login could write after logout cancellation");
        }
    }
    public static void Run(Assembly assembly) {
        MethodInfo acquire=assembly.GetType("PCL.LauncherSessionLock").GetMethod("TryAcquire", BindingFlags.Static|BindingFlags.NonPublic);
        string prefix="LivingPCL-Audit-"+Guid.NewGuid().ToString("N");
        string primary=prefix+"-primary", secondary=prefix+"-secondary";
        ManualResetEvent ready=new ManualResetEvent(false), release=new ManualResetEvent(false);
        Thread owner=new Thread(()=> { bool made; using(Mutex m=new Mutex(true,primary,out made)) { ready.Set(); release.WaitOne(); m.ReleaseMutex(); } });
        owner.IsBackground=true; owner.Start();
        if(!ready.WaitOne(3000)) throw new Exception("Mutex fixture did not start");
        try { if(acquire.Invoke(null,new object[]{new[]{primary},0})!=null) throw new Exception("Existing official-style owner was ignored"); }
        finally { release.Set(); owner.Join(); }
        IDisposable lease=(IDisposable)acquire.Invoke(null,new object[]{new[]{primary,secondary},0});
        if(lease==null) throw new Exception("Free singleton could not be acquired");
        try { bool made; using(Mutex official=new Mutex(true,primary,out made)) { if(made) throw new Exception("Official-style launcher did not see our singleton"); } }
        finally { lease.Dispose(); }
        ready.Reset(); release.Reset();
        owner=new Thread(()=> { bool made; using(Mutex m=new Mutex(true,secondary,out made)) { ready.Set(); release.WaitOne(); m.ReleaseMutex(); } });
        owner.IsBackground=true; owner.Start(); if(!ready.WaitOne(3000)) throw new Exception("Second mutex fixture did not start");
        try {
            if(acquire.Invoke(null,new object[]{new[]{primary,secondary},0})!=null) throw new Exception("Legacy launcher owner was ignored");
            bool made; using(Mutex check=new Mutex(true,primary,out made)) { if(!made) throw new Exception("Partial acquisition was leaked"); check.ReleaseMutex(); }
        } finally { release.Set(); owner.Join(); }
        Mutex abandoned=null;
        owner=new Thread(()=> { bool made; abandoned=new Mutex(true,primary,out made); });
        owner.Start(); owner.Join();
        lease=(IDisposable)acquire.Invoke(null,new object[]{new[]{primary},0});
        if(lease==null) throw new Exception("Abandoned singleton could not recover");
        lease.Dispose(); abandoned.Dispose(); ready.Dispose(); release.Dispose();
    }
}
'@
[LivingPclMutexAudit]::Run($asm)
[LivingPclMutexAudit]::CheckQueuedCancellation($asm)
$startup=Get-Content -LiteralPath (Join-Path $root 'Plain Craft Launcher 2/Application.xaml.vb') -Raw
if($startup.IndexOf('WaitingMutex(e)') -gt $startup.IndexOf('Settings.ImportLegacyPclPreferences()')) { throw 'Shared settings initialize before singleton acquisition' }
$form=Get-Content -LiteralPath (Join-Path $root 'Plain Craft Launcher 2/FormMain.xaml.vb') -Raw
if($form.Contains('Settings.Set("CacheMsV2OAuthRefresh", Settings.Get')) { throw 'Old startup cache overwrite remains' }
Write-Output 'Audit regressions passed: existing/logout/corrupt V2 protection; legacy upgrade; one-time folder import/removal/reset; persistent defaults; strict certificate boundaries; update retries; canceled session writes; official/legacy/abandoned singleton coordination; shared PCL preference migration and reset.'
