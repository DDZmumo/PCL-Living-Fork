param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# 使用虚构账号和独立的测试注册表分支；不读取真实账号数据，不启动应用窗口。
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$root=Split-Path $PSScriptRoot -Parent
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')) { [Reflection.Assembly]::LoadFrom((Join-Path $root $path)) | Out-Null }
$asm=[Reflection.Assembly]::LoadFrom($ExecutablePath)
$type=$asm.GetType('PCL.Settings',$true)
$flags=[Reflection.BindingFlags]'NonPublic,Static'
$enc=$type.GetMethod('DesEncrypt',$flags);$dec=$type.GetMethod('DesDecrypt',$flags)
$makePlan=$type.GetMethod('PlanPclAccountMigration',$flags);$apply=$type.GetMethod('ApplyPclAccountMigration',$flags)
$entries=$type.GetField('Entries').GetValue($null)
function New-Map { return ,([Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)) }
function Encode([string]$value,[string]$id) { return $enc.Invoke($null,@($value,('PCL'+$id))) }
function Decode([string]$value,[string]$id) { return $dec.Invoke($null,@($value,('PCL'+$id))) }
$official=New-Map;$legacy=New-Map
$oldId='1111-2222-3333-4444';$legacyId='0000-0000-0000-0000';$currentId='AAAA-BBBB-CCCC-DDDD'
$official['LoginMsJson']=Encode '{"NativeOnly":"native-token","Shared":"old-token"}' $oldId
$official['CacheMsV2Name']=Encode 'OldActive' $oldId
$official['CacheMsV2OAuthRefresh']=Encode 'old-refresh' $oldId
$official['CacheAuthClient']=Encode 'auth-client' $oldId
$official['CacheAuthPass']=Encode 'auth-password' $oldId
$official['SystemCount']=Encode '7' $oldId
$legacy['LoginMsJson']=Encode '{"LocalOnly":"local-token","shared":"new-token"}' $legacyId
$legacy['CacheMsV2OAuthRefresh']=Encode 'new-refresh' $legacyId
$legacy['CacheMsV2Name']=Encode 'NewActive' $legacyId
$legacy['LoginLegacyName']=Encode 'PlayerA' $legacyId
$legacy['UiLauncherThemeHide']=Encode '0|1|2|3|4|7' $legacyId
$plan=$makePlan.Invoke($null,@($official,$legacy,[string[]]@($currentId,$oldId),[string[]]@($legacyId),$currentId))
$accounts=Decode $plan.Values['LoginMsJson'] $currentId | ConvertFrom-Json
if(@($accounts.PSObject.Properties).Count -ne 3 -or $accounts.shared -ne 'new-token' -or $accounts.NativeOnly -ne 'native-token') { throw 'Account list merge failed' }
if((Decode $plan.Values['CacheMsV2Name'] $currentId) -ne 'NewActive') { throw 'Cache group selection failed' }
if((Decode $plan.Values['CacheAuthPass'] $currentId) -ne 'auth-password') { throw 'Historical auth encryption conversion failed' }
if($plan.Values.ContainsKey('SystemCount') -or $plan.DeleteLegacy.Contains('UiLauncherThemeHide')) { throw 'Excluded settings were included' }
$testPath='Software\LivingPCL-CompatibilityTests\'+[Guid]::NewGuid().ToString('N')
$testNative=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testPath+'\Native')
$testLegacy=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testPath+'\Legacy')
try {
 foreach($kv in $official.GetEnumerator()) { $testNative.SetValue($kv.Key,$kv.Value) }
 foreach($kv in $legacy.GetEnumerator()) { $testLegacy.SetValue($kv.Key,$kv.Value) }
 $apply.Invoke($null,@($plan,$testNative,$testLegacy)) | Out-Null
 foreach($name in $plan.DeleteLegacy) { if($null -ne $testLegacy.GetValue($name,$null)) { throw 'Old account storage was retained' } }
 if($null -eq $testLegacy.GetValue('UiLauncherThemeHide',$null)) { throw 'Excluded state removed' }
 $native2=New-Map;$legacy2=New-Map
 foreach($name in $testNative.GetValueNames()) { $native2[$name]=[string]$testNative.GetValue($name) }
 foreach($name in $testLegacy.GetValueNames()) { $legacy2[$name]=[string]$testLegacy.GetValue($name) }
 $again=$makePlan.Invoke($null,@($native2,$legacy2,[string[]]@($currentId,$oldId),[string[]]@($legacyId),$currentId))
 if($again.Values.Count -ne 0 -or $again.DeleteLegacy.Count -ne 0) { throw 'Migration is not idempotent' }
 $bad=$makePlan.Invoke($null,@($official,$legacy,[string[]]@($currentId,$oldId),[string[]]@($legacyId),$currentId))
 $bad.Values['CacheAuthPass']='invalid-cipher'
 $snapshot=@{};foreach($name in $testNative.GetValueNames()) { $snapshot[$name]=$testNative.GetValue($name) }
 $failed=$false
 try { $apply.Invoke($null,@($bad,$testNative,$testLegacy)) | Out-Null } catch { $failed=$true }
 if(-not $failed) { throw 'Invalid migration was accepted' }
 foreach($name in $snapshot.Keys) { if($snapshot[$name] -cne $testNative.GetValue($name)) { throw 'Rollback failed' } }
 Write-Output 'Synthetic migration: account merge, cache grouping, official DES format, old-value deletion, exclusions, idempotence and rollback passed.'
} finally {
 $testNative.Dispose();$testLegacy.Dispose()
 if(-not $testPath.StartsWith('Software\LivingPCL-CompatibilityTests\')) { throw 'Invalid test cleanup path' }
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testPath,$false)
}

$updater=$asm.GetType('PCL.ModLauncherUpdate',$true)
$new=$updater.GetMethod('NewerRelease',$flags)
foreach($case in @(@('v1.0.1','1.0.0',$true),@('v1.0.1.0','1.0.1',$false),@('v0.9.9','1.0.1',$false),@('v1.0.2-beta','1.0.1',$false))) {
 if($new.Invoke($null,@($case[0],$case[1])) -ne $case[2]) { throw 'Version comparison failed' }
}
$valid=$updater.GetMethod('ValidAssetUrl',$flags)
if(-not $valid.Invoke($null,@('https://github.com/DDZmumo/LivingPCL2-Re/releases/download/v1.0.1/LivingPCL2-Re-1.0.1.exe'))) { throw 'Own release URL rejected' }
foreach($url in @('http://github.com/DDZmumo/LivingPCL2-Re/releases/download/v1.0.1/a.exe','https://github.com/Other/PCL/releases/download/v1.0.1/a.exe','https://example.com/a.exe','https://github.com/DDZmumo/LivingPCL2-Re-other/releases/download/a.exe')) {
 if($valid.Invoke($null,@($url))) { throw 'Untrusted release URL accepted' }
}
$hash=[string](Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
$manifest=$hash.ToLowerInvariant()+'  LivingPCL2-Re-1.0.1.exe'
$parsed=$updater.GetMethod('ManifestHash',$flags).Invoke($null,@($manifest,'LivingPCL2-Re-1.0.1.exe'))
if($parsed -cne $hash.ToLowerInvariant()) { throw 'SHA256 manifest parsing failed' }
$folder=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-UpdateTest-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
$target=Join-Path $folder 'LivingPCL.exe'
try {
 [IO.File]::Copy($ExecutablePath,$target)
 [IO.File]::AppendAllText($target,'synthetic-old-version-overlay')
 $before=[string](Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
 $replace=$updater.GetMethod('ReplaceExecutable',$flags)
 $rejected=$false
 try { $replace.Invoke($null,@($ExecutablePath,[string]$target,('0'*64),$false)) | Out-Null } catch { $rejected=$true }
 if(-not $rejected -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before) { throw 'Invalid update failed to preserve the old EXE' }
 $replace.Invoke($null,@($ExecutablePath,[string]$target,[string]$hash,$false)) | Out-Null
 if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'EXE replacement failed' }
 if(@(Get-ChildItem -LiteralPath $folder).Count -ne 1) { throw 'Update left temporary files' }
 Write-Output 'Updater: versions, own-repository URLs, SHA256 parsing, EXE replacement and invalid-update preservation passed.'
} finally {
 if([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
 [IO.Directory]::Delete($folder,$false)
}
