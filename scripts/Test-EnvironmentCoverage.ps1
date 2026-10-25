param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Old/new restore coverage is tested only in an isolated GUID directory.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$history=$asm.GetType('PCL.EnvironmentHistory');$changes=$asm.GetType('PCL.RecentEnvironmentChanges')
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='coverage-fixture'
function Call([type]$type,[string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 return ,$type.GetMethod($name,$flags).Invoke($null,$values)
}
function Put([string]$path,[string]$value){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$value,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
function PointPath([string]$version,[string]$id){return Join-Path (Call $history 'Store' @($version)) ('points/'+$id+'.json')}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclCoverage-'+[Guid]::NewGuid().ToString('N'));$token=[Threading.CancellationToken]::None
$extra=@('coremods','configureddefaults','global_packs','openloader')
try{
 $history.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'history'),$null)
 $mc=Join-Path $fixture 'minecraft';$legacy=Join-Path $mc 'versions/legacy'
 Put (Join-Path $legacy 'legacy.json') '{"id":"legacy"}';Put (Join-Path $legacy 'config/setting.toml') 'old-setting'
 $id=Call $history 'Capture' @($legacy,$legacy,'legacy fixture',$token,$null,'legacy')
 $record=PointPath $legacy $id;$point=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($record));$point.Remove('coverageVersion')|Out-Null;[IO.File]::WriteAllText($record,$point.ToString())
 Call $history 'MarkHealthy' @($legacy,$id,$true)|Out-Null
 foreach($folder in $extra){Put (Join-Path $legacy ($folder+'/preserve.dat')) ('untracked-'+$folder)}
 Put (Join-Path $legacy 'config/setting.toml') 'changed-setting';Put (Join-Path $legacy 'mods/added.jar') 'added'
 Call $history 'Restore' @($legacy,$legacy,$mc,$id,$token,$null,$null)|Out-Null
 foreach($folder in $extra){$file=Join-Path $legacy ($folder+'/preserve.dat');if(-not [IO.File]::Exists($file) -or [IO.File]::ReadAllText($file) -cne ('untracked-'+$folder)){throw ('Old point deleted an originally untracked directory: '+$folder)}}
 if([IO.File]::ReadAllText((Join-Path $legacy 'config/setting.toml')) -cne 'old-setting' -or [IO.File]::Exists((Join-Path $legacy 'mods/added.jar'))){throw 'Legacy tracked files did not restore normally'}
 $report=Call $changes 'Build' @($legacy,$legacy,$token)
 if($report.Changes.Count -ne 0){throw 'Legacy crash comparison reports untracked directories as new changes'}
 $modern=Join-Path $mc 'versions/modern';Put (Join-Path $modern 'modern.json') '{"id":"modern"}'
 $newId=Call $history 'Capture' @($modern,$modern,'empty extra directories',$token,$null,'modern')
 $newRecord=PointPath $modern $newId;$newPoint=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($newRecord))
 if($newPoint['coverageVersion'].ToObject([int]) -ne 2){throw 'New point lacks explicit extended coverage'}
 Call $history 'MarkHealthy' @($modern,$newId,$true)|Out-Null
 foreach($folder in $extra){Put (Join-Path $modern ($folder+'/added.dat')) ('added-'+$folder)}
 $newReport=Call $changes 'Build' @($modern,$modern,$token)
 foreach($folder in $extra){if(-not @($newReport.Changes|Where-Object {$_.Relative -eq ('g/'+$folder+'/added.dat')}).Count){throw ('New crash comparison omitted '+$folder)}}
 Call $history 'Restore' @($modern,$modern,$mc,$newId,$token,$null,$null)|Out-Null
 foreach($folder in $extra){if([IO.File]::Exists((Join-Path $modern ($folder+'/added.dat')))){throw ('New point failed to restore an explicitly covered empty directory: '+$folder)}}
 $newPoint['coverageVersion']=[Newtonsoft.Json.Linq.JValue]::new(99);[IO.File]::WriteAllText($newRecord,$newPoint.ToString())
 Put (Join-Path $modern 'coremods/preserve-unknown.jar') 'do-not-touch'
 Reject {Call $history 'Restore' @($modern,$modern,$mc,$newId,$token,$null,$null)} 'Unknown coverage version was guessed'
 if([IO.File]::ReadAllText((Join-Path $modern 'coremods/preserve-unknown.jar')) -cne 'do-not-touch'){throw 'Unknown coverage modified the environment'}
 Write-Output 'Coverage passed: legacy scope preserves untracked directories, modern scope restores empty directories, comparisons follow baseline scope, and unknown coverage is rejected without mutation.'
}finally{
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclCoverage-') -and [IO.Directory]::Exists($full)){Remove-Item -LiteralPath $full -Recurse -Force}
}
