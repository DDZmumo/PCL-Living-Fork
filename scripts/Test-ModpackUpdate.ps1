param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Synthetic packs and isolated directories only. No real games or accounts.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance';$type=$asm.GetType('PCL.ModpackUpdate',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='pack-update-fixture'
function Call([string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try {return ,$type.GetMethod($name,$flags).Invoke($null,$values)} catch {throw ($name+": "+$_.Exception.ToString())}
}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
function Zip-Entry($zip,[string]$name,[string]$text){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write($text)}finally{$writer.Dispose()}}
function Pack([string]$name,[hashtable]$overrides,[hashtable]$downloads=@{}){
 $path=Join-Path $fixture ($name+'.mrpack');$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try{
  $files=@();foreach($key in $downloads.Keys){$tmp=Join-Path $fixture 'hash-input';Put $tmp $downloads[$key];$hash=(Call 'Digest' @($tmp,'sha1')).Split(':')[1];$files+=@{path=$key;hashes=@{sha1=$hash};downloads=@('https://example.invalid/'+$key);fileSize=$downloads[$key].Length}}
  $manifest=@{formatVersion=1;game='minecraft';versionId=$name;name='Fixture pack';dependencies=@{minecraft='1.20.1';'fabric-loader'='0.16.0'};files=@($files)}|ConvertTo-Json -Depth 12
  Zip-Entry $zip 'modrinth.index.json' $manifest
  foreach($key in $overrides.Keys){Zip-Entry $zip ('overrides/'+$key) $overrides[$key]}
 }finally{$zip.Dispose()}
 return $path
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclPackUpdate-'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($fixture)|Out-Null
$token=[Threading.CancellationToken]::None
try{
 $game=Join-Path $fixture 'game';$version=Join-Path $fixture 'version'
 $oldFiles=@{'config/author.txt'='old';'config/custom.txt'='author';'options.txt'='keys-old';'xaerowaypoints/server/map.txt'='old-waypoint';'mods/removed.jar'='old-mod';'mods/disabled.jar'='disabled-by-player'}
 $old=Call 'ReadPackage' @((Pack 'old' $oldFiles @{'mods/stable.jar'='stable'}),(Join-Path $fixture 'old-stage'),$token,$null)
 $baseline=Call 'Manifest' @($old)
 foreach($key in $oldFiles.Keys){if($key -ne 'mods/disabled.jar'){Put (Join-Path $game $key) $oldFiles[$key]}}
 Put (Join-Path $game 'mods/disabled.jar.disabled') 'disabled-by-player'
 Put (Join-Path $game 'mods/stable.jar') 'stable'
 Put (Join-Path $game 'mods/personal.jar') 'personal'
 Put (Join-Path $game 'config/custom.txt') 'player-change'
 Put (Join-Path $game 'options.txt') 'personal-keys'
 $newFiles=@{'config/author.txt'='new';'config/custom.txt'='new-author';'options.txt'='keys-new';'xaerowaypoints/server/map.txt'='new-waypoint';'mods/disabled.jar'='new-disabled'}
 $next=Call 'ReadPackage' @((Pack 'new' $newFiles @{'mods/stable.jar'='stable';'mods/new.jar'='download-new'}),(Join-Path $fixture 'new-stage'),$token,$null)
 $plan=Call 'Plan' @($game,$baseline,$next)
 if(@($plan.Changes|Where-Object Conflict).Count -ne 4){throw 'Personal changes/options/waypoints/deletion were not preserved'}
 if(@($plan.Changes|Where-Object {$_.Relative -eq 'mods/stable.jar'}).Count){throw 'Unchanged file was scheduled for download'}
 $script:downloadCount=0
 $downloadType=[Action[System.Collections.Generic.List[PCL.ModNet+NetFile],Threading.CancellationToken]]
 $download={param($files,$cancel) foreach($file in $files){$script:downloadCount++;Put $file.LocalPath 'download-new'}} -as $downloadType
 Call 'DownloadChanges' @($plan,(Join-Path $fixture 'downloads'),$token,$download)|Out-Null
 if($script:downloadCount -ne 1){throw 'Unchanged or conflicting files were downloaded'}
 $backup=Call 'Commit' @($plan,$version,$token,$null,$null)
 foreach($pair in @(@('mods/personal.jar','personal'),@('config/custom.txt','player-change'),@('options.txt','personal-keys'),@('xaerowaypoints/server/map.txt','old-waypoint'),@('config/author.txt','new'),@('mods/new.jar','download-new'))){if([IO.File]::ReadAllText((Join-Path $game $pair[0])) -cne $pair[1]){throw ('Incorrect update result: '+$pair[0])}}
 if([IO.File]::Exists((Join-Path $game 'mods/removed.jar')) -or [IO.File]::Exists((Join-Path $game 'mods/disabled.jar'))){throw 'Removed/disabled mod handling failed'}
 if(-not [IO.File]::Exists((Join-Path $backup 'transaction.json'))){throw 'Durable update backup missing'}
 $saved=Call 'ReadManifest' @($version)
 $nextPlan=Call 'Plan' @($game,$saved,$next)
 if(@($nextPlan.Changes|Where-Object {-not $_.Conflict}).Count){throw 'Alternating updates did not preserve personal changes'}
 # Stale preview must abort before making changes.
 $stale=Call 'Plan' @($game,$saved,(Call 'ReadPackage' @((Pack 'stale' @{'config/author.txt'='third'}),(Join-Path $fixture 'stale-stage'),$token,$null)))
 Put (Join-Path $game 'config/author.txt') 'external-edit'
 Reject {Call 'Commit' @($stale,$version,$token,$null,$null)} 'Stale preview overwrote an external edit'
 # Commit failure after one replacement must restore all original bytes.
 $clean=Join-Path $fixture 'rollback-game';Put (Join-Path $clean 'a.txt') 'old-a';Put (Join-Path $clean 'b.txt') 'old-b'
 $oldRollback=Call 'ReadPackage' @((Pack 'rollback-old' @{'a.txt'='old-a';'b.txt'='old-b'}),(Join-Path $fixture 'rollback-old-stage'),$token,$null)
 $newRollback=Call 'ReadPackage' @((Pack 'rollback-new' @{'a.txt'='new-a';'b.txt'='new-b'}),(Join-Path $fixture 'rollback-new-stage'),$token,$null)
 $rollbackPlan=Call 'Plan' @($clean,(Call 'Manifest' @($oldRollback)),$newRollback)
 $fail=[Action[int]]{param($index) if($index -eq 1){throw 'injected failure'}}
 Reject {Call 'Commit' @($rollbackPlan,(Join-Path $fixture 'rollback-version'),$token,$null,$fail)} 'Injected commit failure was ignored'
 if([IO.File]::ReadAllText((Join-Path $clean 'a.txt')) -cne 'old-a' -or [IO.File]::ReadAllText((Join-Path $clean 'b.txt')) -cne 'old-b'){throw 'Rollback did not restore original files'}
 foreach($unsafe in @('../outside','/absolute','C:/outside','mods/../outside','mods/con.jar','mods/file.jar:stream','mods/a.')){Reject {Call 'RelativePath' @($unsafe)} 'Unsafe pack path accepted'}
 $bad=Pack 'traversal' @{'../escaped.txt'='bad'}
 Reject {Call 'ReadPackage' @($bad,(Join-Path $fixture 'bad-stage'),$token,$null)} 'Archive traversal accepted'
 # A renamed incoming mod must not duplicate a personally installed Fabric mod.
 $personalJar=Join-Path $game 'mods/personal-id.jar';$zip=[IO.Compression.ZipFile]::Open($personalJar,[IO.Compression.ZipArchiveMode]::Create)
 try{Zip-Entry $zip 'fabric.mod.json' '{"schemaVersion":1,"id":"fixture_same_mod","version":"personal"}'}finally{$zip.Dispose()}
 $incoming=Join-Path $fixture 'incoming.jar';$zip=[IO.Compression.ZipFile]::Open($incoming,[IO.Compression.ZipArchiveMode]::Create)
 try{Zip-Entry $zip 'fabric.mod.json' '{"schemaVersion":1,"id":"fixture_same_mod","version":"author-new"}'}finally{$zip.Dispose()}
 $collisionPack=Call 'ReadPackage' @((Pack 'collision' @{}),(Join-Path $fixture 'collision-stage'),$token,$null)
 $fileType=$asm.GetType('PCL.PackUpdateFile');$mod=[Activator]::CreateInstance($fileType);$mod.Relative='mods/new-id.jar';$mod.Hash=Call 'Digest' @($incoming,'sha1');$mod.Payload=$incoming;$collisionPack.Files.Add($mod.Relative,$mod)
 $collisionPlan=Call 'Plan' @($game,$saved,$collisionPack)
 Call 'DownloadChanges' @($collisionPlan,(Join-Path $fixture 'collision-download'),$token,$download)|Out-Null
 if(-not ($collisionPlan.Changes|Where-Object {$_.Relative -eq 'mods/new-id.jar'}).Conflict){throw 'Renamed mod duplicated a personal mod id'}
 # Interrupted transaction recovery restores only expected before/after states.
 $logPath=Join-Path $backup 'transaction.json';$log=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($logPath));$log['complete']=[Newtonsoft.Json.Linq.JValue]::new($false)
 # Use a separate small fixture to avoid later edits from the stale-preview test.
 $recoverRoot=Join-Path $fixture 'recover-root';$recoverVersion=Join-Path $fixture 'recover-version';Put (Join-Path $recoverRoot 'a.txt') 'old-a';Put (Join-Path $recoverRoot 'b.txt') 'old-b'
 $recoverPlan=Call 'Plan' @($recoverRoot,(Call 'Manifest' @($oldRollback)),$newRollback)
 $recoverBox=Call 'Commit' @($recoverPlan,$recoverVersion,$token,$null,$null)
 $recoverLog=Join-Path $recoverBox 'transaction.json';$log=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($recoverLog));$log['complete']=[Newtonsoft.Json.Linq.JValue]::new($false);[IO.File]::WriteAllText($recoverLog,$log.ToString())
 if((Call 'Pending' @($recoverVersion)).Count -ne 1){throw 'Interrupted transaction was not detected'}
 Call 'Recover' @($recoverVersion,$recoverRoot)|Out-Null
 if([IO.File]::ReadAllText((Join-Path $recoverRoot 'a.txt')) -cne 'old-a' -or (Call 'Pending' @($recoverVersion)).Count){throw 'Interrupted transaction recovery failed'}
 # CurseForge uses project/file ids, hashes and the same downloader/merge path.
 $cfPath=Join-Path $fixture 'fixture-cf.zip';$zip=[IO.Compression.ZipFile]::Open($cfPath,[IO.Compression.ZipArchiveMode]::Create)
 try{Zip-Entry $zip 'manifest.json' '{"name":"CF fixture","version":"2","minecraft":{"version":"1.20.1","modLoaders":[{"id":"fabric-0.16.0","primary":true}]},"files":[{"projectID":123,"fileID":456,"required":true}],"overrides":"overrides"}'}finally{$zip.Dispose()}
 $sha=(Call 'Digest' @((Join-Path $game 'mods/stable.jar'),'sha1')).Split(':')[1]
 $cfResponse=@{data=@(@{id=456;gameId=432;modId=123;displayName='Fixture mod';fileName='fixture.jar';fileDate='2026-01-01T00:00:00Z';releaseType=1;downloadCount=1;downloadUrl='https://example.invalid/fixture.jar';fileLength=6;gameVersions=@('1.20.1','Fabric');dependencies=@();modules=@(@{name='fabric.mod.json'});hashes=@(@{algo=1;value=$sha})})}|ConvertTo-Json -Depth 15
 $request=[Func[string,System.Net.Http.HttpMethod,string,Newtonsoft.Json.Linq.JToken]]{param($url,$method,$body) if($url -cne 'https://api.curseforge.com/v1/mods/files' -or $method.Method -cne 'POST'){throw 'Unexpected CurseForge request'};return ,([Newtonsoft.Json.Linq.JToken]::Parse($cfResponse))}
 $cfPack=Call 'ReadPackage' @($cfPath,(Join-Path $fixture 'cf-stage'),$token,$request)
 if($cfPack.Files['mods/fixture.jar'].Identity -cne 'curseforge:123' -or $cfPack.Files['mods/fixture.jar'].Hash -cne ('sha1:'+$sha)){throw 'CurseForge author metadata missing'}
 # Previously unselected optional components stay unselected.
 $optionalPack=Call 'ReadPackage' @((Pack 'optional' @{}),(Join-Path $fixture 'optional-stage'),$token,$null)
 $optional=[Activator]::CreateInstance($fileType);$optional.Relative='mods/optional.jar';$optional.Hash=$mod.Hash;$optional.Payload=$incoming;$optional.OptionalFile=$true;$optionalPack.Files.Add($optional.Relative,$optional)
 $optionalPlan=Call 'Plan' @($game,$saved,$optionalPack)
 if(-not ($optionalPlan.Changes|Where-Object {$_.Relative -eq 'mods/optional.jar'}).Conflict){throw 'Unselected optional mod was installed'}
 Write-Output 'Pack update fixtures passed: author-file changes, personal additions/edits/deletions, keybinds/waypoints, changed-only downloads, repeat update, backups, stale previews, rollback and traversal rejection.'
}finally{
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PclPackUpdate-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}