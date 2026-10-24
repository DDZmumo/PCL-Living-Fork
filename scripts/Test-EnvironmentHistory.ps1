param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Synthetic packs and isolated directories only. No real games or accounts.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance';$type=$asm.GetType('PCL.EnvironmentHistory',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='pack-update-fixture'
function Call([string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try {return ,$type.GetMethod($name,$flags).Invoke($null,$values)} catch {throw ($name+": "+$_.Exception.ToString())}
}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclEnvironment-'+[Guid]::NewGuid().ToString('N'))
$token=[Threading.CancellationToken]::None
try{
 $type.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'history'),$null)
 $mc=Join-Path $fixture 'minecraft';$version=Join-Path $mc 'versions/example';$game=$version
 $library=Join-Path $mc 'libraries/fixture/core.jar';Put $library 'library-old'
 $dependencies=[Collections.Generic.Dictionary[string,string]]::new();$dependencies.Add('libraries/fixture/core.jar',$library)
 Put (Join-Path $version 'example.json') '{"id":"example","inheritsFrom":"1.20.1"}'
 Put (Join-Path $version 'example.jar') 'minecraft-old'
 Put (Join-Path $version 'PCL/config.json') '{"MinecraftInstallManifest":{"loader":"old"}}'
 Put (Join-Path $version 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 Put (Join-Path $game 'mods/old.jar') 'mod-old'
 Put (Join-Path $game 'mods/disabled.jar.disabled') 'disabled'
 Put (Join-Path $game 'config/author.toml') 'old-config'
 Put (Join-Path $game 'options.txt') 'old-keybinds'
 Put (Join-Path $game 'saves/world/level.dat') 'world-current'
 Put (Join-Path $game 'screenshots/keep.png') 'screenshot'
 Put (Join-Path $game 'config/xaero/waypoints.txt') 'map-current'
 $first=Call 'Capture' @($version,$game,'before update',$token,$dependencies,'Minecraft 1.20.1 + old loader')
 $point=(Call 'Points' @($version))[0]
 if([bool]$point['healthy']){throw 'Untested snapshot marked healthy'}
 Call 'MarkHealthy' @($version,$first,$false)|Out-Null
 if([bool](Call 'Points' @($version))[0]['healthy']){throw 'Failed launch marked healthy'}
 Call 'MarkHealthy' @($version,$first,$true)|Out-Null
 if(-not [bool](Call 'Points' @($version))[0]['healthy']){throw 'Successful run not marked'}
 $objectsBefore=@(Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Directory.Name -eq 'objects'}).Count
 $second=Call 'Capture' @($version,$game,'same environment',$token,$dependencies,'same')
 $objectsAfter=@(Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Directory.Name -eq 'objects'}).Count
 if($objectsBefore -ne $objectsAfter){throw 'Identical snapshot duplicated payloads'}
 Put (Join-Path $version 'example.json') '{"id":"example","inheritsFrom":"1.21.1"}'
 Put (Join-Path $version 'example.jar') 'minecraft-new'
 Put (Join-Path $version 'PCL/config.json') '{"MinecraftInstallManifest":{"loader":"new"}}'
 Put (Join-Path $game 'mods/old.jar') 'mod-new'
 Put (Join-Path $game 'mods/new.jar') 'added-mod'
 Put (Join-Path $game 'config/author.toml') 'new-config'
 Put (Join-Path $game 'config/new.toml') 'added-config'
 Put (Join-Path $game 'options.txt') 'new-keybinds'
 Put (Join-Path $game 'saves/world/level.dat') 'world-progress'
 Put (Join-Path $game 'config/xaero/waypoints.txt') 'map-progress'
 Remove-Item -LiteralPath $library
 $undo=Call 'Restore' @($version,$game,$mc,$first,$token,$null,$null)
 foreach($pair in @(@('example.jar','minecraft-old'),@('mods/old.jar','mod-old'),@('config/author.toml','old-config'),@('options.txt','old-keybinds'),@('saves/world/level.dat','world-progress'),@('screenshots/keep.png','screenshot'),@('config/xaero/waypoints.txt','map-progress'))){if([IO.File]::ReadAllText((Join-Path $game $pair[0])) -cne $pair[1]){throw ('Whole environment restore mismatch: '+$pair[0])}}
 if([IO.File]::Exists((Join-Path $game 'mods/new.jar')) -or [IO.File]::Exists((Join-Path $game 'config/new.toml'))){throw 'New environment files were left behind'}
 if([IO.File]::ReadAllText($library) -cne 'library-old'){throw 'Offline dependency restoration failed'}
 if([IO.File]::ReadAllText((Join-Path $version 'example.json')) -notmatch '1.20.1' -or [IO.File]::ReadAllText((Join-Path $version 'PCL/config.json')) -notmatch 'old'){throw 'Minecraft/loader combination not restored'}
 Call 'Restore' @($version,$game,$mc,$undo,$token,$null,$null)|Out-Null
 if([IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod' -or [IO.File]::ReadAllText((Join-Path $game 'options.txt')) -cne 'new-keybinds'){throw 'Undo of whole restore failed'}
 $fail=[Action[int]]{param($index) if($index -eq 1){throw 'injected failure'}}
 Reject {Call 'Restore' @($version,$game,$mc,$first,$token,$fail,$null)} 'Failed restore was accepted'
 if([IO.File]::ReadAllText((Join-Path $version 'example.jar')) -cne 'minecraft-new' -or [IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod'){throw 'Failed restore did not roll back'}
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 Reject {Call 'Restore' @($version,$game,$mc,$first,$cancel.Token,$null,$null)} 'Canceled restore was applied'
 Reject {Call 'Restore' @($version,(Join-Path $fixture 'other-game'),$mc,$first,$token,$null,$null)} 'Isolation mismatch was accepted'
 # Deleting a point removes it from the list but preserves shared objects and game files.
 $blobCount=@(Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Directory.Name -eq 'objects'}).Count
 Reject {Call 'DeletePoints' @($version,[string[]]@($first,'../invalid'))} 'Invalid batch deletion was accepted'
 if(-not ((Call 'Points' @($version))|Where-Object {$_['id'].ToString() -eq $first})){throw 'Invalid batch partly deleted a valid point'}
 $deleted=Call 'DeletePoints' @($version,[string[]]@($second))
 if((Call 'Points' @($version))|Where-Object {$_['id'].ToString() -eq $second}){throw 'Deleted restore point remained visible'}
 if(-not [IO.File]::Exists((Join-Path $deleted ($second+'.json')))){throw 'Deleted point record is not recoverable'}
 if(@(Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Directory.Name -eq 'objects'}).Count -ne $blobCount){throw 'Deleting a point removed shared payloads'}
 if([IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod'){throw 'Deleting a point modified game files'}
 # Corrupt snapshot data must fail before replacing any environment files.
 $point=(Call 'Points' @($version))|Where-Object {$_['id'].ToString() -eq $first}|Select-Object -First 1
 $hash=$point['files']['v/example.jar'].ToString().Substring(5)
 $blob=Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Name -eq $hash}|Select-Object -First 1
 Put $blob.FullName 'corrupted'
 Reject {Call 'Restore' @($version,$game,$mc,$first,$token,$null,$null)} 'Corrupt snapshot was restored'
 if([IO.File]::ReadAllText((Join-Path $version 'example.jar')) -cne 'minecraft-new'){throw 'Corruption rejection modified the environment'}
 # Exercise the real McInstance adapter rather than only the filesystem engine.
 $actual=Join-Path $mc 'versions/adapter'
 Put (Join-Path $actual 'adapter.json') '{"id":"adapter","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{},"arguments":{"game":[],"jvm":[]}}'
 Put (Join-Path $actual 'adapter.jar') 'fixture-client'
 Put (Join-Path $actual 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance');$instance=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$actual))
 $instanceType.GetProperty('Version').SetValue($instance,[PCLCS.McVersion]::new('1.20.1',$true),$null)
 foreach($entry in $settings.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$instance.PathVersion]=$entry.DefaultValue}
 $settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
 $adapterPoint=Call 'CaptureInstance' @($instance,'adapter',$token)
 if([string]::IsNullOrEmpty($adapterPoint)){throw 'Real instance snapshot adapter failed'}
 Write-Output 'Environment history passed: full Minecraft/loader/mod/config restore, added-file removal, world/screenshot/map preservation, offline dependencies, deduplication, healthy status, undo, failure rollback, cancellation, isolation guard and corruption rejection.'
}finally{
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PclEnvironment-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}