param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Temporary options/world fixtures only. Never starts Minecraft or reads real profiles.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$data=$asm.GetType('PCL.LocalGameData',$true);$sync=$asm.GetType('PCL.PersonalGameSettings',$true);$quick=$asm.GetType('PCL.QuickPlayWorlds',$true)
$instanceType=$asm.GetType('PCL.ModMinecraft+McInstance',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue}
$settings['Identify'].ValueCache['']='synthetic-local-game-features'
function Call($type,[string]$method,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 return ,$type.GetMethod($method,$flags).Invoke($null,$values)
}
function Field($object,[string]$name){return $object.GetType().GetField($name,$flags).GetValue($object.PSObject.BaseObject)}
function Set-Field($object,[string]$name,$value){$object.GetType().GetField($name,$flags).SetValue($object.PSObject.BaseObject,$value.PSObject.BaseObject)}
function Put([string]$path,[string]$value){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$value,[Text.UTF8Encoding]::new($false))}
function Assert-Rejected([scriptblock]$work,[string]$message){$rejected=$false;try{& $work|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $message}}
function Instance([string]$name,[string]$version){
 $folder=Join-Path $fixture ('.minecraft/versions/'+$name)
 [IO.Directory]::CreateDirectory($folder)|Out-Null
 $value=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$folder))
 $instanceType.GetProperty('Version').SetValue($value,[PCLCS.McVersion]::new($version,$true),$null)
 foreach($entry in $settings.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$value.PathVersion]=$entry.DefaultValue}
 $settings['VersionArgumentIndieV2'].ValueCache[$value.PathVersion]=$true
 Put (Join-Path $folder 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 return $value
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclLocalGameFeatures-'+[Guid]::NewGuid().ToString('N'))
try{
 $data.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'personal-data'),$null)
 $source=Instance 'source' '1.20.1';$target=Instance 'target' '1.20.1';$legacy=Instance 'legacy' '1.12.2'
 Put (Join-Path $source.PathVersion 'options.txt') "mouseSensitivity:0.8`r`nfov:0.3`r`nkey_key.forward:key.keyboard.r`r`nshowSubtitles:true`r`nrenderDistance:9`r`nresourcePacks:[`"author-source.zip`"]`r`n"
 $original="mouseSensitivity:0.2`r`nfov:0.0`r`nkey_key.forward:key.keyboard.w`r`nshowSubtitles:false`r`nrenderDistance:32`r`nresourcePacks:[`"author-target.zip`"]`r`nkey_mod.magic:key.keyboard.v`r`n"
 $targetFile=Join-Path $target.PathVersion 'options.txt';Put $targetFile $original
 $settings['LaunchPersonalOptionsSync'].ValueCache['']=$true
 Call $sync 'ImportFrom' @($source)|Out-Null
 $session=Call $sync 'Prepare' @($target)
 $merged=[IO.File]::ReadAllText($targetFile)
 if($merged -notmatch 'mouseSensitivity:0.8' -or $merged -notmatch 'key_key.forward:key.keyboard.r' -or $merged -notmatch 'showSubtitles:true'){throw 'Personal options were not applied'}
 foreach($line in @('renderDistance:32','resourcePacks:["author-target.zip"]','key_mod.magic:key.keyboard.v')){if(-not $merged.Contains($line)){throw 'Author/custom options were changed'}}
 if(-not $merged.EndsWith("`r`n") -or [IO.File]::ReadAllText($targetFile+'.pcl-backup') -cne $original){throw 'Original newlines or backup not preserved'}
 Put $targetFile ($merged.Replace('mouseSensitivity:0.8','mouseSensitivity:0.6').Replace('renderDistance:32','renderDistance:20'))
 Call $sync 'Finish' @($session)|Out-Null
 $profile=Call $data 'ReadJson' @('personal-options.json')
 if($profile['values']['mouseSensitivity'].ToString() -cne '0.6' -or $profile['values']['renderDistance']){throw 'Bidirectional capture included non-personal fields'}
 $settings['VersionPersonalOptionsSync'].ValueCache[$target.PathVersion]=1
 if($null -ne (Call $sync 'Prepare' @($target))){throw 'Version exception was ignored'}
 $settings['VersionPersonalOptionsSync'].ValueCache[$target.PathVersion]=0
 Put (Join-Path $legacy.PathVersion 'options.txt') "key_key.forward:17`nmouseSensitivity:0.1`n"
 $legacySession=Call $sync 'Prepare' @($legacy)
 if([IO.File]::ReadAllText((Join-Path $legacy.PathVersion 'options.txt')) -notmatch 'key_key.forward:19'){throw 'Legacy/modern key translation failed'}
 $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$keys.Add('key_key.forward')|Out-Null
 $unknown=[Newtonsoft.Json.Linq.JObject]::Parse('{"key_key.forward":"key.keyboard.unrecognized"}')
 if($null -eq $keys -or $null -eq $unknown){throw 'Missing key test input'}
 if($null -ne (Call $sync 'ForVersion' @('key_key.forward','key.keyboard.unrecognized',$false))){throw 'Unknown key unexpectedly translated'}
 $safe=Call $sync 'MergeText' @("key_key.forward:17`n",$unknown,$false,$keys,$false)
 if([string]$safe -cne "key_key.forward:17`n"){throw 'Unknown legacy key replaced a valid binding'}
 $viewKeys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$viewKeys.Add('fov')|Out-Null
 $view=[Newtonsoft.Json.Linq.JObject]::Parse('{"fov":"0.5"}')
 if([string](Call $sync 'MergeText' @("fov:70`n",$view,$true,$viewKeys,$true)) -cne "fov:90`n"){throw 'Normalized FOV was not converted to degrees'}
 $view=[Newtonsoft.Json.Linq.JObject]::Parse('{"fov":"90"}')
 if([string](Call $sync 'MergeText' @("fov:0.0`n",$view,$false,$viewKeys,$false)) -cne "fov:0.5`n"){throw 'Degree FOV was not converted for older options'}
 $tracked=Call $sync 'Prepare' @($target)
 Call $sync 'Track' @($tracked,[Diagnostics.Process]::GetCurrentProcess())|Out-Null
 if($null -ne (Call $sync 'Prepare' @($target))){throw 'Options were applied to a directory with a live game session'}
 $journalName='options-sessions/'+(Field $tracked 'Id')+'.json'
 $journal=Call $data 'ReadJson' @($journalName);$journal['pid']=[Newtonsoft.Json.Linq.JValue]::new(2147483646);$journal['started']=[Newtonsoft.Json.Linq.JValue]::new([long]1)
 Call $data 'WriteJson' @($journalName,$journal)|Out-Null
 Put $targetFile ([IO.File]::ReadAllText($targetFile).Replace('mouseSensitivity:0.6','mouseSensitivity:0.7'))
 Call $sync 'RecoverExitedSessions' @()|Out-Null
 $profile=Call $data 'ReadJson' @('personal-options.json')
 if($profile['values']['mouseSensitivity'].ToString() -cne '0.7'){throw 'Exited-session recovery failed'}
 $bytes=[Text.Encoding]::UTF8.GetBytes('unexpected-new-data')
 Assert-Rejected {Call $data 'AtomicWrite' @($targetFile,$bytes,'incorrect-old-hash',$true)} 'Concurrent options edit was overwritten'
 foreach($pair in @(@('1.19.4',$false),@('1.20.1',$true),@('26.2',$true))){if((Call $quick 'Supported' @((Instance ('version-'+$pair[0]) $pair[0]))) -ne $pair[1]){throw 'Quick Play version gate failed'}}
 $world=Join-Path $target.PathVersion 'saves/World with spaces';Put (Join-Path $world 'level.dat') 'synthetic-world';Put (Join-Path $world 'region/keep.txt') 'world-data-preserve'
 Assert-Rejected {Call $quick 'WorldPath' @($target.PathVersion,'../outside')} 'World traversal was accepted'
 $arguments=[Collections.Generic.List[string]]::new();$arguments.Add('--quickPlayMultiplayer');$arguments.Add('example.invalid');$arguments.Add('--server');$arguments.Add('example.invalid')
 $context=Call $quick 'Prepare' @($target,'World with spaces',$arguments,$false)
 if($arguments.Contains('--quickPlayMultiplayer') -or $arguments.Contains('--server') -or -not $arguments.Contains('--quickPlaySingleplayer') -or $arguments[$arguments.IndexOf('--quickPlaySingleplayer')+1] -cne 'World with spaces'){throw 'World Quick Play conflicted with server arguments or split the folder name'}
 $log=Field $context 'LogFile'
 Put $log '[{"type":"singleplayer","identifier":"World with spaces","name":"A recent world","lastPlayedTime":"2026-10-01T12:00:00Z"}]'
 Call $quick 'Finish' @($context)|Out-Null
 $recent=Call $quick 'Recent' @()
 if($recent.Count -ne 1 -or (Field $recent[0] 'VersionPath') -cne $target.PathVersion -or (Field $recent[0] 'Identifier') -cne 'World with spaces'){throw 'Recent world lost its version or folder association'}
 if([IO.File]::ReadAllText((Join-Path $world 'region/keep.txt')) -cne 'world-data-preserve'){throw 'Quick Play changed a world file'}
 [IO.File]::Delete((Join-Path $world 'level.dat'))
 if((Call $quick 'Recent' @()).Count -ne 0){throw 'Missing world was retained or recreated'}
 $bindings=$asm.GetType('PCL.InstanceKeybindings',$true)
 Put $targetFile ([IO.File]::ReadAllText($targetFile)+"key_fixture.action:key.keyboard.r`r`nkey_other.action:key.keyboard.r`r`nkey_unknown.action:key.keyboard.t`r`n")
 foreach($mod in @('fixture','other')){
  $jar=Join-Path $target.PathVersion ('mods/'+$mod+'.jar');[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($jar))|Out-Null
  $archive=[IO.Compression.ZipFile]::Open($jar,[IO.Compression.ZipArchiveMode]::Create)
  try{
   $writer=[IO.StreamWriter]::new($archive.CreateEntry('fabric.mod.json').Open());try{$writer.Write('{"id":"'+$mod+'","name":"Example '+$mod+'"}')}finally{$writer.Dispose()}
   $writer=[IO.StreamWriter]::new($archive.CreateEntry('assets/'+$mod+'/lang/en_us.json').Open());try{$writer.Write('{"'+$mod+'.action":"Example action"}')}finally{$writer.Dispose()}
  }finally{$archive.Dispose()}
 }
 $rows=Call $bindings 'ReadBindings' @($target.PathVersion,[Threading.CancellationToken]::None)
 $entry=@($rows|Where-Object {(Field $_ 'Key') -eq 'key_fixture.action'})[0]
 if((Field $entry 'SharedCount') -ne 3 -or (Field $entry 'Owners') -notmatch 'Example fixture'){throw 'Shared-key or local mod ownership identification failed'}
 $unknownEntry=@($rows|Where-Object {(Field $_ 'Key') -eq 'key_unknown.action'})[0]
 if((Field $unknownEntry 'SharedCount') -ne 2 -or (Field $unknownEntry 'Owners') -match 'Example'){throw 'Default chat binding conflict or unknown owner handling failed'}
 $before=[IO.File]::ReadAllText($targetFile)
 Call $bindings 'Save' @($target.PathVersion,'key_fixture.action','key.keyboard.r','key.keyboard.y')|Out-Null
 if([IO.File]::ReadAllText($targetFile) -cne $before.Replace('key_fixture.action:key.keyboard.r','key_fixture.action:key.keyboard.y')){throw 'Rebinding changed unrelated options'}
 Assert-Rejected {Call $bindings 'Save' @($target.PathVersion,'key_fixture.action','key.keyboard.r','key.keyboard.u')} 'A stale binding overwrote a newer edit'
 if([string](Call $sync 'NormalizeBinding' @('19:ALT')) -cne 'key.keyboard.r:ALT' -or [string](Call $sync 'BindingForVersion' @('key.keyboard.r:ALT',$false)) -cne '19:ALT'){throw 'Modifier or legacy binding conversion failed'}
 Write-Output 'Local game features passed: personal-field merge, author/custom-option preservation, backup/newlines, bidirectional changes, version exception, legacy keys/FOV, active/exited sessions, atomic conflicts, Quick Play paths/arguments/missing worlds, shared-key/mod ownership, rebinding and stale-edit protection. No real games, accounts or user profiles were used.'
}finally{
 $data.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclLocalGameFeatures-[a-f0-9]{32}\z'){throw 'Unsafe local-feature fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
