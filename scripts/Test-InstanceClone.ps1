param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# GUID fixtures only. Does not start Minecraft or read a real account file.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $repo $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$engine=$asm.GetType('PCL.InstanceCloner',$true)
$history=$asm.GetType('PCL.EnvironmentHistory',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue}
$settings['Identify'].ValueCache['']='instance-clone-fixture'
function Invoke-Engine([type]$type,[string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try{return ,$type.GetMethod($name,$flags).Invoke($null,$values)}catch{throw ($name+': '+$_.Exception.ToString())}
}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$rejected=$false;try{& $action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $message}}
function Snapshot([string]$folder){$result=@{};foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File){$result[$file.FullName.Substring($folder.Length)]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash};return $result}
function Same([hashtable]$before,[hashtable]$after,[string]$message){if($before.Count -ne $after.Count){throw $message};foreach($key in $before.Keys){if($before[$key] -cne $after[$key]){throw ($message+': '+$key)}}}
function Options([bool]$saves,[bool]$screenshots,[bool]$servers){$o=[Activator]::CreateInstance($asm.GetType('PCL.InstanceCloneOptions'),$true);$o.CopySaves=$saves;$o.CopyScreenshots=$screenshots;$o.CopyServers=$servers;return $o}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclClone-'+[Guid]::NewGuid().ToString('N'))
$token=[Threading.CancellationToken]::None
$junction=$null
try{
 $history.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'history'),$null)
 $asm.GetType('PCL.LocalGameData').GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'local'),$null)
 $mc=Join-Path $fixture 'minecraft';$source=Join-Path $mc 'versions/original';$game=$source
 Put (Join-Path $source 'original.json') '{"id":"original","inheritsFrom":"1.20.1","jar":"original","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"arguments":{"game":[],"jvm":[]}}'
 Put (Join-Path $mc 'versions/1.20.1/1.20.1.json') '{"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}'
 Put (Join-Path $mc 'versions/1.21.1/1.21.1.json') '{"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}'
 Put (Join-Path $source 'original.jar') 'fixture-client'
 Put (Join-Path $source 'PCL/Setup.ini') "VersionArgumentIndieV2:True`r`nVersionRamCustom:23`r`nCacheMsAccess:fixture-only-private`r`nVersionAdvanceRun:fixture-command"
 Put (Join-Path $source 'PCL/config.json') '{"MinecraftInstallManifest":{"fixture":"old"},"AccountToken":"fixture-only-private"}'
 Put (Join-Path $source 'mods/example-1.0.jar') 'old-mod'
 Put (Join-Path $source 'mods/off.jar.disabled') 'off-mod'
 Put (Join-Path $source 'config/example.toml') 'fixture-setting=old'
 Put (Join-Path $source 'options.txt') 'key_key.jump:key.keyboard.space'
 Put (Join-Path $source 'resourcepacks/example.zip') 'resource-pack'
 Put (Join-Path $source 'shaderpacks/example.zip') 'shader-pack'
 Put (Join-Path $source 'saves/world/level.dat') 'original-world'
 Put (Join-Path $source 'screenshots/fixture.png') 'original-screenshot'
 Put (Join-Path $source 'servers.dat') 'original-servers'
 Put (Join-Path $source 'servers.dat_old') 'original-servers-old'
 Put (Join-Path $source 'logs/latest.log') 'skip-log'
 Put (Join-Path $source 'original-natives/native.dll') 'skip-native'
 Put (Join-Path $source 'PCL/PackUpdateBackups/old.bak') 'skip-backup'
 Put (Join-Path $source 'launcher_accounts.json') '{"fixture":"never-copy"}'
 Put (Join-Path $source 'config/accounts.json') '{"fixture":"never-copy"}'
 Put (Join-Path $source 'mods/.connector/cache.jar') 'skip-cache'
 foreach($directory in @('coremods','configureddefaults','global_packs','openloader')){Put (Join-Path $source ($directory+'/review.txt')) ('original-'+$directory)}
 Put (Join-Path $source 'config/xaero/waypoints.txt') 'original-waypoint'
 $before=Snapshot $source
 $clone=Invoke-Engine $engine 'Clone' @($source,$game,'test-minimal',(Options $false $false $false),$token,$null)
 $dest=$clone.VersionFolder.TrimEnd('\')
 $session=Join-Path $fixture 'local/world-backup-sessions/review-active.json'
 $current=[Diagnostics.Process]::GetCurrentProcess()
 try {
  Put $session ((@{gameFolder=$source;pid=$current.Id;started=$current.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json -Compress))
  Reject {Invoke-Engine $engine 'Clone' @($source,$game,'active-session',(Options $false $false $false),$token,$null)} 'Persisted live game session was ignored during cloning'
  Reject {Invoke-Engine $engine 'PreviewMigration' @($dest,$source,$token)} 'Persisted live source session was ignored during migration'
 }finally{if([IO.File]::Exists($session)){Remove-Item -LiteralPath $session};$current.Dispose()}
 foreach($name in @('test-minimal.json','test-minimal.jar','mods/example-1.0.jar','mods/off.jar.disabled','config/example.toml','options.txt','resourcepacks/example.zip','shaderpacks/example.zip','PCL/CloneOrigin.json')){if(-not [IO.File]::Exists((Join-Path $dest $name))){throw ('Missing clone file: '+$name)}}
 foreach($name in @('saves','screenshots','servers.dat','servers.dat_old','logs','original-natives','launcher_accounts.json','config/accounts.json','mods/.connector','PCL/PackUpdateBackups')){if(Test-Path -LiteralPath (Join-Path $dest $name)){throw ('Excluded data was copied: '+$name)}}
 $json=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((Join-Path $dest 'test-minimal.json')))
 if($json['id'].ToString() -cne 'test-minimal' -or $json['jar'].ToString() -cne 'test-minimal' -or $json['inheritsFrom'].ToString() -cne '1.20.1'){throw 'Clone JSON id/jar/inheritance mismatch'}
 $setup=[IO.File]::ReadAllText((Join-Path $dest 'PCL/Setup.ini'))
 if($setup -notmatch 'VersionArgumentIndieV2:True' -or $setup -notmatch 'VersionRamCustom:23' -or $setup -match 'fixture-only-private|fixture-command'){throw 'Isolation or setup sanitization failed'}
 if([IO.File]::ReadAllText((Join-Path $dest 'PCL/config.json')) -match 'AccountToken|fixture-only-private'){throw 'Private JSON setting was copied'}
 Same $before (Snapshot $source) 'Cloning modified the original version'
 Put (Join-Path $dest 'mods/example-1.0.jar') 'clone-only-mod'
 if([IO.File]::ReadAllText((Join-Path $source 'mods/example-1.0.jar')) -cne 'old-mod'){throw 'Editable clone file shares storage with original'}
 $full=Invoke-Engine $engine 'Clone' @($source,$game,'test-full',(Options $true $true $true),$token,$null)
 foreach($name in @('saves/world/level.dat','screenshots/fixture.png','servers.dat','servers.dat_old')){if(-not [IO.File]::Exists((Join-Path $full.VersionFolder $name))){throw ('Selected data missing: '+$name)}}
 Same $before (Snapshot $source) 'Full cloning modified the original version'
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'original',(Options $false $false $false),$token,$null)} 'Clone overwrote original'
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'test-full',(Options $false $false $false),$token,$null)} 'Clone overwrote existing target'
 foreach($bad in @('../escape','CON','COM0','bad;name','.pcl-clone-user')){Reject {Invoke-Engine $engine 'Clone' @($source,$game,$bad,(Options $false $false $false),$token,$null)} ('Invalid clone name accepted: '+$bad)}
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'canceled',(Options $false $false $false),$cancel.Token,$null)} 'Canceled clone succeeded'
 $midCancel=[Threading.CancellationTokenSource]::new();$cancelAction=[Action[int]]{param($i) if($i -eq 1){$midCancel.Cancel()}}
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'mid-canceled',(Options $false $false $false),$midCancel.Token,$cancelAction)} 'Mid-copy cancellation succeeded'
 $mutate=[Action[int]]{param($i) if($i -eq 1){Put (Join-Path $source 'config/new-during-copy.toml') 'external-edit'}}
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'changed-source',(Options $false $false $false),$token,$mutate)} 'Inconsistent source was cloned'
 Remove-Item -LiteralPath (Join-Path $source 'config/new-during-copy.toml')
 if(@(Get-ChildItem -LiteralPath (Join-Path $mc 'versions') -Force|Where-Object {$_.Name -like '.pcl-clone-*' -or $_.Name -in @('canceled','mid-canceled','changed-source')}).Count){throw 'Canceled/failed clone left a stage or visible version'}
 $outside=Join-Path $fixture 'outside';Put (Join-Path $outside 'keep.txt') 'keep-outside'
 $junction=Join-Path $source 'config/linked'
 New-Item -ItemType Junction -Path $junction -Target $outside|Out-Null
 Reject {Invoke-Engine $engine 'Clone' @($source,$game,'linked-source',(Options $false $false $false),$token,$null)} 'Linked source directory was cloned'
 [IO.Directory]::Delete($junction);$junction=$null
 if([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -cne 'keep-outside'){throw 'Link rejection modified external data'}
 # A non-isolated source gets a fully independent game directory.
 $sharedSource=Join-Path $mc 'versions/shared'
 Put (Join-Path $sharedSource 'shared.json') '{"id":"shared","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}'
 Put (Join-Path $mc 'mods/shared.jar') 'shared-game-mod'
 $shared=Invoke-Engine $engine 'Clone' @($sharedSource,$mc,'shared-test',(Options $false $false $false),$token,$null)
 if([IO.File]::ReadAllText((Join-Path $shared.VersionFolder 'mods/shared.jar')) -cne 'shared-game-mod'){throw 'Shared source game environment was not isolated'}
 # Migration requires a fresh preview, an original restore point and a backup hook for MC changes.
 foreach($directory in @('coremods','configureddefaults','global_packs','openloader')){Put (Join-Path $dest ($directory+'/review.txt')) ('clone-'+$directory)}
 Put (Join-Path $dest 'config/xaero/waypoints.txt') 'clone-waypoint'
 Put (Join-Path $dest 'test-minimal.json') '{"id":"test-minimal","inheritsFrom":"1.21.1","jar":"test-minimal","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"arguments":{"game":[],"jvm":[]}}'
 $plan=Invoke-Engine $engine 'PreviewMigration' @($dest,$source,$token)
 if($plan.OldMinecraft -cne '1.20.1' -or $plan.NewMinecraft -cne '1.21.1' -or $plan.Changes.Count -lt 2){throw 'Migration preview omitted MC or mod changes'}
 Same $before (Snapshot $source) 'Migration preview modified source'
 Reject {Invoke-Engine $engine 'ApplyMigration' @($plan,$token,$null,$null,$null)} 'MC-changing migration ran without a world backup hook'
 $script:backupCalled=$false
 $backup=[Action[string,string]]{param($old,$new) $script:backupCalled=$true}
 $capture=[Func[string]]{if(-not $script:backupCalled){throw 'Environment capture ran before world backup'};return (Invoke-Engine $history 'Capture' @($source,$source,'migration-before',$token,$null,'fixture migration'))}
 $fail=[Action[int]]{param($i) if($i -eq 1){throw 'injected transaction failure'}}
 Reject {Invoke-Engine $engine 'ApplyMigration' @($plan,$token,$capture,$backup,$fail)} 'Injected migration failure succeeded'
 # Transaction backups are new local metadata; all game, config and launcher setting bytes stay intact after rollback.
 foreach($key in $before.Keys){$file=$source+$key;if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $before[$key]){throw ('Failed migration did not roll back: '+$key)}}
 $plan=Invoke-Engine $engine 'PreviewMigration' @($dest,$source,$token)
 $undo=Invoke-Engine $engine 'ApplyMigration' @($plan,$token,$capture,$backup,$null)
 if([string]::IsNullOrEmpty($undo) -or -not $script:backupCalled){throw 'Successful migration lacks restore point or world backup callback'}
 if([IO.File]::ReadAllText((Join-Path $source 'mods/example-1.0.jar')) -cne 'clone-only-mod'){throw 'Migration did not apply tested mod'}
 if([IO.File]::ReadAllText((Join-Path $source 'config/xaero/waypoints.txt')) -cne 'original-waypoint'){throw 'Migration replaced personal map markers'}
 foreach($directory in @('coremods','configureddefaults','global_packs','openloader')){if([IO.File]::ReadAllText((Join-Path $source ($directory+'/review.txt'))) -cne ('clone-'+$directory)){throw 'Additional environment directory was not migrated'}}
 $migrated=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((Join-Path $source 'original.json')))
 if($migrated['id'].ToString() -cne 'original' -or $migrated['jar'].ToString() -cne 'original' -or $migrated['inheritsFrom'].ToString() -cne '1.21.1'){throw 'Migrated version JSON references wrong instance'}
 if([IO.File]::ReadAllText((Join-Path $source 'PCL/config.json')) -notmatch 'AccountToken'){throw 'Migration deleted unrelated local account configuration'}
 foreach($pair in @(@('saves/world/level.dat','original-world'),@('screenshots/fixture.png','original-screenshot'),@('servers.dat','original-servers'))){if([IO.File]::ReadAllText((Join-Path $source $pair[0])) -cne $pair[1]){throw ('Migration changed personal data: '+$pair[0])}}
 $stale=Invoke-Engine $engine 'PreviewMigration' @($dest,$source,$token)
 Put (Join-Path $source 'config/example.toml') 'external-change-after-preview'
 Reject {Invoke-Engine $engine 'ApplyMigration' @($stale,$token,$capture,$backup,$null)} 'Stale migration plan was accepted'
 Invoke-Engine $history 'Restore' @($source,$source,$mc,$undo,$token,$null,$null)|Out-Null
 foreach($directory in @('coremods','configureddefaults','global_packs','openloader')){if([IO.File]::ReadAllText((Join-Path $source ($directory+'/review.txt'))) -cne ('original-'+$directory)){throw 'Migration undo failed to restore the complete environment'}}
 if([IO.File]::ReadAllText((Join-Path $source 'config/xaero/waypoints.txt')) -cne 'original-waypoint'){throw 'Migration undo modified personal map markers'}
 $probe=Join-Path $mc 'versions/isolation-probe';[IO.Directory]::CreateDirectory((Join-Path $probe 'mods/1.12.2'))|Out-Null
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance');$instance=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$probe));$instanceType.GetProperty('Version').SetValue($instance,[PCLCS.McVersion]::new('1.20.1',$true),$null)
 $settings['LaunchArgumentIndieV2'].ValueCache['']=0
 $root=Invoke-Engine $engine 'GameRootForInstance' @($instance)
 if([IO.Path]::GetFullPath($root).TrimEnd('\') -cne [IO.Path]::GetFullPath($mc).TrimEnd('\')){throw 'Nested mods folder incorrectly enabled isolation'}
 [IO.Directory]::CreateDirectory((Join-Path $probe 'saves'))|Out-Null;Put (Join-Path $probe 'saves/not-a-world.txt') 'file-only'
 $root=Invoke-Engine $engine 'GameRootForInstance' @($instance)
 if([IO.Path]::GetFullPath($root).TrimEnd('\') -cne [IO.Path]::GetFullPath($mc).TrimEnd('\')){throw 'File in saves incorrectly enabled isolation'}
 Put (Join-Path $probe 'mods/direct.jar') 'direct-file'
 if([IO.Path]::GetFullPath((Invoke-Engine $engine 'GameRootForInstance' @($instance))).TrimEnd('\') -cne [IO.Path]::GetFullPath($probe).TrimEnd('\')){throw 'Direct mod did not enable isolation'}
 Remove-Item -LiteralPath (Join-Path $probe 'mods/direct.jar')
 [IO.Directory]::CreateDirectory((Join-Path $probe 'saves/world-directory'))|Out-Null
 if([IO.Path]::GetFullPath((Invoke-Engine $engine 'GameRootForInstance' @($instance))).TrimEnd('\') -cne [IO.Path]::GetFullPath($probe).TrimEnd('\')){throw 'Direct save directory did not enable isolation'}
 Write-Output 'Instance clone passed: isolated independent files, optional worlds/screenshots/servers, JSON and JAR remapping, original byte preservation, exclusions, invalid names, overwrite rejection, cancellation cleanup, source-change detection, reparse rejection, shared-root isolation, explicit migration preview, required world-backup hook, restore point, rollback and stale-plan rejection.'
}finally{
 if($junction -and [IO.Directory]::Exists($junction)){[IO.Directory]::Delete($junction)}
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PclClone-')){if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}}
}
