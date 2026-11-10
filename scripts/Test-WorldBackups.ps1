param([string]$ExecutablePath,[switch]$EngineOnly)
# Only GUID-named temporary worlds and local profiles are touched. No game or launcher is started.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Get-FixedLfSource.ps1')
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclWorldBackups-'+[Guid]::NewGuid().ToString('N'))
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$token=[Threading.CancellationToken]::None
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,[string]$message){$caught=$false;try{& $action|Out-Null}catch{$caught=$true};if(-not $caught){throw $message}}
function AssertText([string]$path,[string]$text){if(-not [IO.File]::Exists($path) -or [IO.File]::ReadAllText($path) -cne $text){throw ('Unexpected world contents: '+$path)}}
function Call([string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try{return ,$type.GetMethod($name,$flags).Invoke($null,$values)}catch{throw ($name+': '+$_.Exception.ToString())}
}
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null
 [Reflection.Assembly]::LoadFrom((Join-Path $root 'Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll'))|Out-Null
 [Reflection.Assembly]::LoadFrom((Join-Path $root 'MeloongCore/Shared/bin/MeloongCore.dll'))|Out-Null
 if($EngineOnly){
  # Compile the actual VB facade and LocalGameData adapter; all engine business
  # code runs from the current PCLCS DLL. Only launcher/UI services are inert stubs.
  # Never build the launcher, load UI assemblies or read build credentials here.
  $core=Join-Path $root 'PCLCS/bin/PCLCS.dll'
  if(-not [IO.File]::Exists($core)){throw 'Build PCLCS/PCLCS.csproj before running the isolated backup engine.'}
  $coreAssembly=[Reflection.Assembly]::LoadFrom($core)
  $backupEngineType=$coreAssembly.GetType('PCLCS.WorldBackupEngine`1',$false)
  if($null -eq $backupEngineType){throw 'Build the current PCLCS library containing WorldBackupEngine before -EngineOnly.'}
  $engine=(Read-LfFixedSourceText -Name 'backups-adapter-a1')
  $uiBoundary=[regex]::Matches($engine,'(?m)^Friend NotInheritable Class WorldBackupsUi\r?$')
  if($uiBoundary.Count -ne 1){throw 'Expected exactly one production backup UI boundary for isolated facade compilation.'}
  $engine=$engine.Substring(0,$uiBoundary[0].Index)
  Put (Join-Path $fixture 'Engine.vb') $engine
  Put (Join-Path $fixture 'LocalGameData.vb') ((Read-LfFixedSourceText -Name 'localdata-adapter-a1'))
  $stubs=@'
Imports System
Imports System.IO
Imports System.Diagnostics
Imports System.Collections.Generic
Imports Newtonsoft.Json.Linq
Friend Class McInstance
 Public PathIndie As String
 Public PathVersion As String
 Public Sub New(Value As String)
  PathIndie = Value
  PathVersion = Value
 End Sub
End Class
Friend Module ModWatcher
 Public McWatcherList As New List(Of Watcher)
 Public HasRunningMinecraft As Boolean
 Public Class Watcher
  Public GameProcess As Process
  Public Instance As McInstance
  Public State As MinecraftState
  Public Enum MinecraftState
   Loading
   Running
   Crashed
   Ended
   Canceled
  End Enum
 End Class
End Module
Friend Class WorldBackupsUi
 Friend Shared IsBusy As Boolean
End Class
Friend Class Settings
 Friend Shared AutoEnabled As Boolean = True
 Friend Shared Retention As Integer = 5
 Friend Shared Function [Get](Of T)(Name As String, Optional Instance As McInstance = Nothing) As T
  Return DirectCast(Convert.ChangeType(If(Name = "VersionWorldBackupAutoEnabled", CObj(AutoEnabled), CObj(Retention)), GetType(T)), T)
 End Function
End Class
Friend Class PathUtils
 Friend Shared Function RemoveExtendedPrefix(Value As String) As String
  Return Value
 End Function
End Class
Friend Class InstanceResourceStore
 Friend Shared Sub EnsureWithin(Root As String, FileName As String)
  If Not Path.GetFullPath(FileName).StartsWith(Path.GetFullPath(Root).TrimEnd("\"c) & "\", StringComparison.OrdinalIgnoreCase) Then Throw New IOException()
 End Sub
End Class
Friend Class WorldMetadata
 Friend Name As String
 Friend Shared Function ReadDetails(Folder As String) As WorldMetadata
  Return Nothing
 End Function
End Class
Friend Class Logger
 Friend Shared Sub Warn(ErrorValue As Exception, Message As String)
 End Sub
End Class
Friend Enum HintType
 Red
End Enum
Friend Module TestUi
 Friend Sub RunInUi(Action As Action)
  Action()
 End Sub
 Friend Sub OpenExplorer(Value As String)
 End Sub
 Friend Sub Hint(Value As String, Optional Kind As HintType = HintType.Red)
 End Sub
End Module
'@
  Put (Join-Path $fixture 'Stubs.vb') $stubs
  $compiler=Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/Roslyn/vbc.exe'
  if(-not (Test-Path -LiteralPath $compiler)){
   $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
   if(Test-Path -LiteralPath $vswhere){$compiler=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/Roslyn/vbc.exe'|Select-Object -First 1}
  }
  if(-not $compiler -or -not (Test-Path -LiteralPath $compiler)){throw 'VB compiler not found; use -ExecutablePath after the normal launcher build.'}
  $framework=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
  $output=Join-Path $fixture 'WorldBackupEngine.dll'
  $references=@('System.dll','System.Core.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')|ForEach-Object {Join-Path $framework $_}
  $references+=Join-Path $root 'Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll'
  $references+=Join-Path $root 'MeloongCore/Shared/bin/MeloongCore.dll'
  $references+=$core
  $references+=Join-Path $framework 'Facades/netstandard.dll'
  $compilerArgs=@('/nologo','/target:library','/rootnamespace:PCL','/imports:System,System.Collections.Generic,System.Diagnostics,System.Linq,System.Threading,Newtonsoft.Json.Linq,MeloongCore',('/out:'+$output),('/sdkpath:'+$framework),('/reference:'+($references -join ',')),(Join-Path $fixture 'Engine.vb'),(Join-Path $fixture 'LocalGameData.vb'),(Join-Path $root 'Plain Craft Launcher 2/Modules/Base/FileTaskProgress.vb'),(Join-Path $fixture 'Stubs.vb'))
  & $compiler @compilerArgs
  if($LASTEXITCODE -ne 0){throw 'Isolated world-backup engine compilation failed'}
  $asm=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($output))
  $service=$asm.GetType('PCL.WorldBackups',$true).GetField('Service',$flags)
  if($null -eq $service -or -not $service.FieldType.IsGenericType -or $service.FieldType.GetGenericTypeDefinition() -ne $backupEngineType){throw 'The isolated production facade is not bound to the current C# backup engine.'}
 }else{
  if(-not $ExecutablePath){throw 'Specify -ExecutablePath or -EngineOnly.'}
  Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
  foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
  $asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
  $settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
  foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue}
  $settings['Identify'].ValueCache['']='world-backup-fixture'
 }
 $type=$asm.GetType('PCL.WorldBackups',$true)
 $local=$asm.GetType('PCL.LocalGameData',$true)
 $backupRoot=Join-Path $fixture 'backups';$profileRoot=Join-Path $fixture 'profile'
 $type.GetProperty('TestRoot',$flags).SetValue($null,$backupRoot,$null)
 $local.GetProperty('TestRoot',$flags).SetValue($null,$profileRoot,$null)
 $game=Join-Path $fixture 'minecraft/versions/fixture';$world=Join-Path $game 'saves/World A';$other=Join-Path $game 'saves/World B'
 Put (Join-Path $world 'level.dat') 'world-old'
 Put (Join-Path $world 'region/r.0.0.mca') 'region-old'
 Put (Join-Path $world 'session.lock') 'fixture-lock'
 [IO.Directory]::CreateDirectory((Join-Path $world 'empty-folder'))|Out-Null
 Put (Join-Path $other 'level.dat') 'other-world-keep'
 $first=Call 'CaptureWorld' @($game,'World A','manual',5,$token,$null)
 $otherId=Call 'CaptureWorld' @($game,'World B','manual',5,$token,$null)
 $points=Call 'Points' @($game)
 $point=$points|Where-Object {$_['id'].ToString() -eq $first}|Select-Object -First 1
 if($points.Count -ne 2 -or $point['world'].ToString() -ne 'World A' -or $point['reason'].ToString() -ne 'manual' -or $point['bytes'].ToObject([long]) -ne 19 -or -not $point['created']){throw 'Backup preview metadata is incomplete'}
 if($point['files']['session.lock']){throw 'Runtime lock was included in backup'}
 $store=Call 'Store' @($game)
 if([IO.Path]::GetFullPath($store).StartsWith([IO.Path]::GetFullPath($game),[StringComparison]::OrdinalIgnoreCase)){throw 'Backups were stored in exportable game contents'}
 Put (Join-Path $world 'level.dat') 'world-progress'
 Put (Join-Path $world 'region/r.0.0.mca') 'region-progress'
 Put (Join-Path $world 'added/new.txt') 'later-file'
 $undo=Call 'Restore' @($game,$first,5,$token,$null)
 AssertText (Join-Path $world 'level.dat') 'world-old';AssertText (Join-Path $world 'region/r.0.0.mca') 'region-old';AssertText (Join-Path $other 'level.dat') 'other-world-keep'
 if([IO.File]::Exists((Join-Path $world 'added/new.txt')) -or -not [IO.Directory]::Exists((Join-Path $world 'empty-folder')) -or -not $undo){throw 'World restoration omitted removals, empty folders, or safety snapshot'}
 Call 'Restore' @($game,$undo,5,$token,$null)|Out-Null
 AssertText (Join-Path $world 'level.dat') 'world-progress';AssertText (Join-Path $world 'added/new.txt') 'later-file'
 # Fail after moving the original and after installing the replacement. Both must roll back.
 foreach($step in @(1,2)){
  $fail=[Action[int]]{param($index) if($index -eq $step){throw 'injected transaction failure'}}
  Reject {Call 'Restore' @($game,$first,50,$token,$fail)} 'Failed restore was accepted'
  AssertText (Join-Path $world 'level.dat') 'world-progress';AssertText (Join-Path $world 'added/new.txt') 'later-file'
 }
 $cancel=[Threading.CancellationTokenSource]::new()
 $cancelAtSwap=[Action[int]]{param($index) if($index -eq 1){$cancel.Cancel()}}
 Reject {Call 'Restore' @($game,$first,50,$cancel.Token,$cancelAtSwap)} 'Canceled restore was accepted'
 AssertText (Join-Path $world 'level.dat') 'world-progress'
 # A real open lock, even without a launcher watcher, blocks backup and restoration.
 Put (Join-Path $world 'session.lock') 'fixture-lock'
 $held=[IO.File]::Open((Join-Path $world 'session.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{
  Reject {Call 'CaptureWorld' @($game,'World A','locked',5,$token,$null)} 'Active world lock was ignored by backup'
  Reject {Call 'Restore' @($game,$first,5,$token,$null)} 'Active world lock was ignored by restore'
 }finally{$held.Dispose()}
 # Persisted live sessions block the operation even when the global flag is false.
 $self=[Diagnostics.Process]::GetCurrentProcess()
 Put (Join-Path $profileRoot 'world-backup-sessions/fixture.json') ('{"gameFolder":'+[Newtonsoft.Json.JsonConvert]::SerializeObject($game)+',"pid":'+$self.Id+',"started":'+$self.StartTime.ToUniversalTime().Ticks+'}')
 if(-not (Call 'HasActiveGame' @($game,$null))){throw 'Persisted live session was ignored'}
 Reject {Call 'Restore' @($game,$first,5,$token,$null)} 'Running game was allowed to restore'
 Remove-Item -LiteralPath (Join-Path $profileRoot 'world-backup-sessions/fixture.json')
 # The current exited Watcher must be ignored while HasRunningMinecraft is still true.
 # Active same-folder Watchers must remain blocked, independently of the legacy flag.
 $instanceType=$asm.GetType($(if($EngineOnly){'PCL.McInstance'}else{'PCL.ModMinecraft+McInstance'}),$true)
 Put (Join-Path $game 'fixture.json') '{"id":"fixture","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{},"arguments":{"game":[],"jvm":[]}}'
 Put (Join-Path $game 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 $instance=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$game))
 if($EngineOnly){
  $asm.GetType('PCL.Settings').GetField('Retention',$flags).SetValue($null,50)
 }else{
  foreach($entry in $settings.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$instance.PathVersion]=$entry.DefaultValue}
  $settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
  $settings['VersionWorldBackupRetention'].ValueCache[$instance.PathVersion]=50
  $settings['VersionWorldBackupAutoEnabled'].ValueCache[$instance.PathVersion]=$true
 }
 $watcherModule=$asm.GetType('PCL.ModWatcher',$true);$watcherType=$asm.GetType('PCL.ModWatcher+Watcher',$true)
 $watchers=$watcherModule.GetField('McWatcherList',$flags).GetValue($null)
 $watcher=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($watcherType)
 if(-not $EngineOnly){
  # Do not run the game-monitoring constructor. Attach the actual C# engine using
  # the actual production Host/Group, so original default Loading state is valid.
  # FormatterServices bypasses the new readonly engine initialization as well.
  $nativeField=$watcherType.GetField('Native',$flags)
  if($null -eq $nativeField){throw 'Actual Watcher engine bridge was not found'}
  $watcherHost=$watcherModule.GetField('Host',$flags).GetValue($null)
  $watcherGroup=$watcherModule.GetField('Group',$flags).GetValue($null)
  $nativeWatcher=$nativeField.FieldType.GetConstructors()[0].Invoke(@($watcher,$watcherHost,$watcherGroup))
  $nativeField.SetValue($watcher,$nativeWatcher)
 }
 $watcherType.GetField('GameProcess',$flags).SetValue($watcher,$self)
 $watcherType.GetField('Instance',$flags).SetValue($watcher,$instance)
 $watchers.Add($watcher)
 if(-not (Call 'HasActiveGame' @($game,$null))){throw 'Active Watcher was ignored'}
 $watchers.Remove($watcher)|Out-Null
 $process=[Diagnostics.Process]::new();$process.StartInfo.FileName='powershell.exe';$process.StartInfo.Arguments='-NoProfile -Command Start-Sleep -Milliseconds 800';$process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
 try{
  $process.Start()|Out-Null
  Call 'Track' @($instance,$process)|Out-Null
  Put (Join-Path $profileRoot ('world-backup-sessions/'+$process.Id+'-1.json')) ('{"gameFolder":'+[Newtonsoft.Json.JsonConvert]::SerializeObject((Join-Path $fixture 'stale-pid-game'))+',"pid":'+$process.Id+',"started":1}')
  if(-not (Call 'HasActiveGame' @($game,$null))){throw 'Tracked game session did not block backups'}
  $process.WaitForExit()
  $watcherType.GetField('GameProcess',$flags).SetValue($watcher,$process)
  $watchers.Add($watcher)
  $watcherModule.GetField('HasRunningMinecraft',$flags).SetValue($null,$true)
  if(Call 'HasActiveGame' @($game,$process)){throw 'Exited Watcher was treated as a live game'}
  $count=(Call 'Points' @($game)).Count
  if($EngineOnly){$instance.PathIndie=Join-Path $fixture 'changed-instance-folder'}else{$settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$false}
  Call 'OnProcessStopped' @($instance,$process,$true)|Out-Null
  if($EngineOnly){$instance.PathIndie=$game}else{$settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true}
  if((Call 'Points' @($game)).Count -ne $count+2){throw 'Normal exit did not back up the launched folder with a stale PID record and the legacy running flag set'}
  Call 'OnProcessStopped' @($instance,$process,$false)|Out-Null
  if((Call 'Points' @($game)).Count -ne $count+2){throw 'Abnormal exit triggered automatic world backup'}
 }finally{
  $watchers.Remove($watcher)|Out-Null
  $watcherModule.GetField('HasRunningMinecraft',$flags).SetValue($null,$false)
  if(-not $process.HasExited){$process.WaitForExit()};$process.Dispose()
 }
 $count=(Call 'Points' @($game)).Count
 if((Call 'BeforeMinecraftChange' @($instance,'1.20.1','1.20.1',$token)).Count -ne 0 -or (Call 'Points' @($game)).Count -ne $count){throw 'Unchanged Minecraft version triggered upgrade backup'}
 $beforeUpdate=Call 'BeforeMinecraftChange' @($instance,'1.20.1','1.21.1',$token)
 if($beforeUpdate.Count -ne 2 -or (Call 'Points' @($game)).Count -ne $count+2){throw 'Minecraft upgrade did not capture worlds first'}
 if($EngineOnly){$asm.GetType('PCL.Settings').GetField('AutoEnabled',$flags).SetValue($null,$false)}else{$settings['VersionWorldBackupAutoEnabled'].ValueCache[$instance.PathVersion]=$false}
 try{if((Call 'BeforeMinecraftChange' @($instance,'1.20.1','1.21.1',$token)).Count -ne 0){throw 'Automatic-backup disable was ignored'}}finally{if($EngineOnly){$asm.GetType('PCL.Settings').GetField('AutoEnabled',$flags).SetValue($null,$true)}else{$settings['VersionWorldBackupAutoEnabled'].ValueCache[$instance.PathVersion]=$true}}
 # Emulate a process interruption immediately after the replacement was installed.
 $transaction=[Guid]::NewGuid().ToString('N');$previous=Join-Path $game ('saves/.pcl-world-'+$transaction+'.before');$stage=Join-Path $game ('saves/.pcl-world-'+$transaction+'.stage')
 [IO.Directory]::CreateDirectory($stage)|Out-Null
 $saved=Call 'Metadata' @($game,$first)
 Call 'ReadArchive' @((Join-Path $store ('points/'+$first)),$saved,$token,$stage,$null)|Out-Null
 Put (Join-Path $store ('transactions/'+$transaction+'.json')) ('{"gameFolder":'+[Newtonsoft.Json.JsonConvert]::SerializeObject($game)+',"world":"World A","point":"'+$first+'","existed":true,"complete":false}')
 [IO.Directory]::Move($world,$previous);[IO.Directory]::Move($stage,$world)
 $owner=[Diagnostics.Process]::new();$owner.StartInfo.FileName='powershell.exe';$owner.StartInfo.Arguments='-NoProfile -Command Start-Sleep -Milliseconds 2500';$owner.StartInfo.UseShellExecute=$false;$owner.StartInfo.CreateNoWindow=$true;$owner.StartInfo.WindowStyle='Hidden'
 try {
  $owner.Start()|Out-Null
  $journalPath=Join-Path $store ('transactions/'+$transaction+'.json');$journal=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($journalPath));$journal['ownerPid']=[Newtonsoft.Json.Linq.JValue]::new($owner.Id);$journal['ownerStarted']=[Newtonsoft.Json.Linq.JValue]::new($owner.StartTime.ToUniversalTime().Ticks)
  [IO.File]::WriteAllText($journalPath,$journal.ToString())
  Reject {Call 'RecoverBeforeLaunch' @($game,$token)} 'Launch recovery took over a restore owned by another live launcher'
  if(-not [IO.Directory]::Exists($previous)){throw 'Active foreign restore was modified'}
  $owner.WaitForExit()
 }finally{$owner.Dispose()}
 Call 'RecoverBeforeLaunch' @($game,$token)|Out-Null
 AssertText (Join-Path $world 'level.dat') 'world-progress';AssertText (Join-Path $world 'added/new.txt') 'later-file'
 if([IO.Directory]::Exists($previous) -or [IO.Directory]::Exists($stage)){throw 'Interrupted restore was not cleaned after rollback'}
 # Filesystem failure and cancellation must not trigger retention.
 $beforeIds=@((Call 'Points' @($game))|ForEach-Object {$_['id'].ToString()})
 $alreadyCanceled=[Threading.CancellationTokenSource]::new();$alreadyCanceled.Cancel()
 Reject {Call 'CaptureWorld' @($game,'World A','canceled',1,$alreadyCanceled.Token,$null)} 'Canceled backup was committed'
 Put (Join-Path $fixture 'blocked-store') 'not-a-directory'
 $type.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'blocked-store'),$null)
 try{Reject {Call 'CaptureWorld' @($game,'World A','filesystem failure',1,$token,$null)} 'Failed destination was accepted'}finally{$type.GetProperty('TestRoot',$flags).SetValue($null,$backupRoot,$null)}
 $afterIds=@((Call 'Points' @($game))|ForEach-Object {$_['id'].ToString()})
 if(@(Compare-Object $beforeIds $afterIds).Count){throw 'Failed or canceled backup pruned existing backups'}
 Reject {Call 'CaptureWorld' @($game,'../escape','traversal',5,$token,$null)} 'World traversal was accepted'
 Reject {Call 'Restore' @($game,'../escape',5,$token,$null)} 'Backup id traversal was accepted'
 Reject {Call 'CaptureWorld' @($game,'World A','invalid retention',0,$token,$null)} 'Invalid retention was accepted'
 # A directory junction must never be followed, including when used as the game root.
 $outside=Join-Path $fixture 'outside';Put (Join-Path $outside 'level.dat') 'outside-keep'
 $junction=Join-Path $game 'saves/Linked'
 New-Item -ItemType Junction -Path $junction -Target $outside|Out-Null
 try{Reject {Call 'CaptureWorld' @($game,'Linked','junction',5,$token,$null)} 'Junction world was accepted'}finally{[IO.Directory]::Delete($junction)}
 AssertText (Join-Path $outside 'level.dat') 'outside-keep'
 # Damaged archives fail before current-world capture or replacement.
 $corrupt=Call 'CaptureWorld' @($game,'World A','corrupt-fixture',50,$token,$null)
 Put (Join-Path $store ('points/'+$corrupt+'/world.zip')) 'broken zip'
 $countBefore=(Call 'Points' @($game)).Count
 Reject {Call 'Restore' @($game,$corrupt,50,$token,$null)} 'Corrupted archive was restored'
 if((Call 'Points' @($game)).Count -ne $countBefore){throw 'Corrupt restore saved or removed a point before validation'}
 AssertText (Join-Path $world 'level.dat') 'world-progress'
 # Tampered manifest paths must fail without writing outside the stage.
 $bad=Call 'CaptureWorld' @($game,'World A','malicious-path-fixture',50,$token,$null)
 $manifestPath=Join-Path $store ('points/'+$bad+'/manifest.json')
 $manifest=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($manifestPath))
 $property=$manifest['files'].Properties()|Select-Object -First 1;$value=$property.Value.DeepClone();$property.Remove();$manifest['files'].Add('../escape.txt',$value)
 Put $manifestPath ($manifest.ToString())
 Reject {Call 'Restore' @($game,$bad,50,$token,$null)} 'Tampered path manifest was restored'
 if([IO.File]::Exists((Join-Path $fixture 'escape.txt'))){throw 'Path traversal escaped the fixture'}
 # Retention is per world, excludes manual backups, and runs only after a verified new backup.
 $legacy=Join-Path $store ('points/'+$first+'/manifest.json');$legacyPoint=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($legacy));$legacyPoint.Remove('manual')|Out-Null;$legacyPoint['reason']=[Newtonsoft.Json.Linq.JValue]::new('手动备份');[IO.File]::WriteAllText($legacy,$legacyPoint.ToString())
 $manual=Call 'CaptureWorld' @($game,'World A','手动备份',2,$token,$null)
 1..3|ForEach-Object {Put (Join-Path $world 'level.dat') ('retention-'+$_);Call 'CaptureWorld' @($game,'World A','retention',2,$token,$null)|Out-Null}
 $points=Call 'Points' @($game)
 $worldPoints=@($points|Where-Object {$_['world'].ToString() -eq 'World A'})
 if(@($worldPoints|Where-Object {$_['id'].ToString() -notin @($first,$manual)}).Count -ne 2 -or -not ($points|Where-Object {$_['id'].ToString() -eq $otherId})){throw 'Retention crossed world boundaries or kept the wrong number of automatic backups'}
 foreach($id in @($first,$manual)){if(-not ($worldPoints|Where-Object {$_['id'].ToString() -eq $id})){throw 'Retention removed a current or legacy manual backup'}}
 $selected=@($points|Where-Object {$_['world'].ToString() -eq 'World A'}|ForEach-Object {$_['id'].ToString()})
 Reject {Call 'DeletePoints' @($game,[string[]]@($selected[0],'../invalid'))} 'Invalid delete batch was accepted'
 if(-not ((Call 'Points' @($game))|Where-Object {$_['id'].ToString() -eq $selected[0]})){throw 'Invalid batch partly removed a valid backup'}
 $deleted=Call 'DeletePoints' @($game,[string[]]@($selected[0]))
 if([IO.Directory]::Exists((Join-Path $deleted ('points/'+$selected[0]))) -or [IO.Directory]::Exists((Join-Path $deleted 'deleted'))){throw 'Deleted backup payload remains'}
 AssertText (Join-Path $world 'level.dat') 'retention-3'
 # Backups now live below each world in the saves page, using native collapsible groups.
 $groupXml=[xml](Read-LfFixedSourceText -Name 'backup-group-xaml-f4')
 $groupSource=(Read-LfFixedSourceText -Name 'backup-group-code-f4')
 $settingsXml=[xml](Read-LfFixedSourceText -Name 'backup-settings-xaml-f4')
 $settingsSource=(Read-LfFixedSourceText -Name 'backup-settings-code-f4')
 foreach($control in @('MyCard','MyIconTextButton')){if(-not $groupXml.SelectSingleNode("//*[local-name()='$control']")){throw ('Missing native backup-group control '+$control)}}
 foreach($control in @('MyCard','MyCheckBox','MyTextBox')){if(-not $settingsXml.SelectSingleNode("//*[local-name()='$control']")){throw ('Missing native backup-settings control '+$control)}}
 if(-not $groupSource.Contains('Row.BindRestorePoint(Point)') -or -not $groupSource.Contains('Row.RestorePoint(') -or -not $groupSource.Contains('CardBackups.SwapControl = PanContent') -or $groupSource -match '\.Tag\s*=' -or -not $settingsSource.Contains('BindOwner') -or -not $settingsSource.Contains('CardSettings.SwapControl = PanSettings')){throw 'Native grouped backup UI contract failed'}
 Write-Output 'World backups passed: streamed archive verification, preview metadata, whole-world restore and safety snapshot, independent worlds, empty folders, removal of newer files, failure/cancellation rollback, interrupted-transaction recovery, session locks, live/exited Watchers, persisted process guard, normal/abnormal exit, upgrade trigger and disable switch, destination failure, path/link rejection, corruption rejection, per-world retention and recoverable batch removal. Native grouped backup UI contract passed.'
}finally{
 if($type){$type.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)}
 if($local){$local.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)}
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^PclWorldBackups-[a-f0-9]{32}$'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
