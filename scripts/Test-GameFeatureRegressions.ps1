param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Replays the real first-run options and Mojang Quick Play JSON shapes in owned temporary files.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='game-regression-fixture'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclGameRegression-'+[Guid]::NewGuid().ToString('N'))
function Call($type,$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 return ,$type.GetMethod($name,$flags).Invoke($null,$values)
}
$data=$asm.GetType('PCL.LocalGameData');$quick=$asm.GetType('PCL.QuickPlayWorlds');$bindings=$asm.GetType('PCL.InstanceKeybindings')
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null;$data.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'profile'),$null)
 $version=Join-Path $fixture '.minecraft/versions/test';$world=Join-Path $version 'saves/Test world';[IO.Directory]::CreateDirectory($world)|Out-Null
 [IO.File]::WriteAllText((Join-Path $world 'level.dat'),'world-fixture')
 [IO.File]::WriteAllText((Join-Path $version 'options.txt'),"lang:zh_cn`n")
 $rows=Call $bindings 'ReadBindings' @($version,[Threading.CancellationToken]::None)
 $blank=$rows.Count -eq 0
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance');$instance=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$version));$instanceType.GetProperty('Version').SetValue($instance,[PCLCS.McVersion]::new('1.20.1',$true),$null)
 $settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
 [IO.Directory]::CreateDirectory((Join-Path $version 'PCL'))|Out-Null;[IO.File]::WriteAllText((Join-Path $version 'PCL/Setup.ini'),'VersionArgumentIndieV2:True')
 $arguments=[Collections.Generic.List[string]]::new();$session=Call $quick 'Prepare' @($instance,$null,$arguments,$false)
 $log=$session.GetType().GetField('LogFile',$flags).GetValue($session)
 [IO.File]::WriteAllText($log,'[{"type":"singleplayer","id":"Test world","name":"Test world","lastPlayedTime":"2026-10-24T00:00:00Z","gamemode":"survival"}]')
 Call $quick 'Finish' @($session)|Out-Null
 $recent=Call $quick 'Recent' @();$missing=$recent.Count -eq 0
 [pscustomobject]@{FirstRunKeyPageBlank=$blank;MojangIdLogLost=$missing}|ConvertTo-Json -Compress
 if($blank -or $missing){throw 'Observed first-run/Quick Play failure reproduced'}
 $defaults=@($rows|Where-Object {$_.GetType().GetField('Saved',$flags).GetValue($_.PSObject.BaseObject) -eq $false})
 if($defaults.Count -lt 20){throw 'First-run defaults were not distinguished from saved bindings'}
 Call $bindings 'Save' @($version,'key_key.forward',$null,'key.keyboard.r')|Out-Null
 if([IO.File]::ReadAllText((Join-Path $version 'options.txt')) -notmatch 'key_key.forward:key.keyboard.r'){throw 'Editing a default did not create only the requested binding'}
 $afterEdit=Call $bindings 'ReadBindings' @($version,[Threading.CancellationToken]::None)
 if($afterEdit.Count -lt 20){throw 'Editing one default hid the remaining default bindings'}
 $history=Call $data 'Address' @('recent-worlds.json');[IO.File]::Delete([string]$history)
 $level=Join-Path $world 'level.dat';$stream=[IO.FileStream]::new($level,[IO.FileMode]::Create,[IO.FileAccess]::Write);$zip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Compress)
 function Write-Name($targetStream,[string]$text){$bytes=[Text.Encoding]::UTF8.GetBytes($text);$targetStream.WriteByte([byte]($bytes.Length -shr 8));$targetStream.WriteByte([byte]($bytes.Length -band 255));$targetStream.Write($bytes,0,$bytes.Length)}
 try{
  $zip.WriteByte(10);Write-Name $zip ''; $zip.WriteByte(10);Write-Name $zip 'Data'
  $zip.WriteByte(8);Write-Name $zip 'LevelName';Write-Name $zip 'Saved display name'
  $zip.WriteByte(4);Write-Name $zip 'LastPlayed';$millis=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();$time=[BitConverter]::GetBytes([long]$millis);if([BitConverter]::IsLittleEndian){[Array]::Reverse($time)};$zip.Write($time,0,8);$zip.WriteByte(0);$zip.WriteByte(0)
 }finally{$zip.Dispose();$stream.Dispose()}
 Call $quick 'DiscoverExistingWorlds' @($instance)|Out-Null
 $recent=Call $quick 'Recent' @()
 if($recent.Count -ne 1 -or $recent[0].GetType().GetField('Name',$flags).GetValue($recent[0].PSObject.BaseObject) -cne 'Saved display name'){throw 'An existing isolated world was not recovered from actual level.dat metadata'}
 'Game regressions passed: first-run default bindings and the verified Mojang id-based log schema.'
}finally{
 $data.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclGameRegression-[a-f0-9]{32}\z'){throw 'Unsafe regression fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
