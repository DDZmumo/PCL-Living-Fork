param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$PreviewDirectory)
# All snapshots, settings, garbage collection and UI bindings use GUID fixtures only.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath));$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance';$history=$asm.GetType('PCL.EnvironmentHistory',$true)
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null);foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue};$settings['Identify'].ValueCache['']='retention-fixture'
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true);$app.InitializeComponent()
[Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
$app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
$app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
[Windows.Application]::ResourceAssembly=$asm
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclBackupRetention-'+[Guid]::NewGuid().ToString('N'));$none=[Threading.CancellationToken]::None
function Call($target,$name,[object[]]$arguments){$values=[object[]]::new($arguments.Count);for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}};if($target -is [Type]){return ,$target.GetMethod($name,$flags).Invoke($null,$values)};return ,$target.GetType().GetMethod($name,$flags).Invoke($target,$values)}
# Only these migrated page states may use a forwarding property; DTO field hooks remain strict.
function Set-RestorePageState($page,[string]$name,$value){
 $target=$page.PSObject.BaseObject;$pageType=$target.GetType()
 if($pageType.FullName -cne 'PCL.PageInstanceRestorePoints' -or $name -cnotin @('Owner','RetentionDirty')){throw 'State hooks are limited to PageInstanceRestorePoints Owner/RetentionDirty'}
 $member=$pageType.GetField($name,$flags)
 if($null -eq $member){$member=$pageType.GetProperty($name,$flags)}
 if($null -eq $member){throw [MissingMemberException]::new($pageType.FullName,$name)}
 if($member -is [Reflection.FieldInfo]){$stateType=$member.FieldType}else{$stateType=$member.PropertyType}
 $expectedType=if($name -ceq 'Owner'){$asm.GetType('PCL.ModMinecraft+McInstance',$true)}else{[bool]}
 if($stateType -ne $expectedType){throw ('Unexpected migrated state type: '+$pageType.FullName+'.'+$name)}
 if($null -ne $value){$value=$value.PSObject.BaseObject}
 if($member -is [Reflection.FieldInfo]){$member.SetValue($target,$value)}else{$member.SetValue($target,$value,$null)}
}
function Put($path,$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Reject([scriptblock]$action,$message){$failed=$false;try{& $action|Out-Null}catch{$failed=$true};if(-not $failed){throw $message}}
function Points{return (Call $history 'Points' @($instance.PathVersion))}
function Capture($label){return (Call $history 'CaptureInstance' @($instance,$label,$none))}
function Manual($point){$values=[object[]]::new(1);$values.SetValue($point,0);return $history.GetMethod('IsManualPoint',$flags).Invoke($null,$values)}
function PointPath($id){return (Join-Path $store ('points/'+$id+'.json'))}
function Blob($point,$key){return (Join-Path $store ('objects/'+$point['files'][$key].ToString().Substring(5)))}
function Render($element,$name,$width,$height){
 if(-not $PreviewDirectory){return};[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory))|Out-Null
 $panel=[Windows.Controls.Grid]::new();$panel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#e7effb');$panel.Children.Add($element)|Out-Null
 $panel.Measure([Windows.Size]::new($width,$height));$panel.Arrange([Windows.Rect]::new(0,0,$width,$height));$panel.UpdateLayout()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($panel);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$out=[IO.File]::Create((Join-Path $PreviewDirectory $name));try{$encoder.Save($out)}finally{$out.Dispose();$panel.Children.Clear()}
}
try{
 $history.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'history'),$null)
 $mc=Join-Path $fixture 'minecraft';$version=Join-Path $mc 'versions/example'
 Put (Join-Path $version 'example.json') '{"id":"example","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{},"arguments":{"game":[],"jvm":[]}}'
 Put (Join-Path $version 'example.jar') 'shared-client'
 Put (Join-Path $version 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 $instance=[PCL.ModMinecraft+McInstance]::new($version);$instance.Version=[PCLCS.McVersion]::new('1.21.11',$true)
 foreach($entry in $settings.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$instance.PathVersion]=$entry.DefaultValue}
 $settings['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
 if($settings['VersionEnvironmentHistoryAutoCleanup'].DefaultValue -ne $false){throw 'Automatic cleanup is enabled without opting in'}
 $settings['VersionEnvironmentHistoryRetention'].ValueCache[$instance.PathVersion]=2
 Put (Join-Path $version 'mods/library.jar') 'manual-one';$manualOne=Capture '手动还原点'
 $store=Call $history 'Store' @($instance.PathVersion)
 $legacy=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((PointPath $manualOne)));$legacy.Remove('manual')|Out-Null;[IO.File]::WriteAllText((PointPath $manualOne),$legacy.ToString())
 $automatic=New-Object 'System.Collections.Generic.List[string]'
 for($i=1;$i -le 4;$i++){Put (Join-Path $version 'mods/library.jar') ('auto-'+$i);$automatic.Add((Capture '游戏启动前'))}
 if(@((Points)|Where-Object {-not (Manual $_)}).Count -ne 4){throw 'Disabled retention pruned existing automatic points'}
 $old=(Points)|Where-Object {$_['id'].ToString() -eq $automatic[0]}|Select-Object -First 1;$oldBlob=Blob $old 'g/mods/library.jar'
 # Legacy automatic records are recognized by the original application label.
 $old.Remove('manual')|Out-Null;[IO.File]::WriteAllText((PointPath $automatic[0]),$old.ToString())
 Put (Join-Path $version 'mods/library.jar') 'manual-two';$manualTwo=Capture '手动还原点'
 $settings['VersionEnvironmentHistoryAutoCleanup'].ValueCache[$instance.PathVersion]=$true
 Put (Join-Path $version 'mods/library.jar') 'auto-five';$latest=Capture '更新模组前'
 $points=Points;$autoPoints=@($points|Where-Object {-not (Manual $_)})
 if($autoPoints.Count -ne 2 -or -not ($autoPoints|Where-Object {$_['id'].ToString() -eq $latest}) -or -not ($autoPoints|Where-Object {$_['id'].ToString() -eq $automatic[3]})){throw 'Retention did not keep the newest automatic points'}
 foreach($id in @($manualOne,$manualTwo)){if(-not ($points|Where-Object {$_['id'].ToString() -eq $id})){throw 'Manual point was counted or removed'}}
 if([IO.File]::Exists($oldBlob)){throw 'Unreferenced payload from an expired automatic point was not reclaimed'}
 foreach($point in $points){foreach($hash in $point['files'].Properties()){if(-not [IO.File]::Exists((Join-Path $store ('objects/'+$hash.Value.ToString().Substring(5))))){throw 'A retained snapshot lost a shared payload'}}}
 # Deleted records remain recoverable and continue protecting their payloads.
 Put (Join-Path $version 'mods/library.jar') 'recoverable-deleted';$removed=Capture '整合包更新前';$deletedPoint=(Points)|Where-Object {$_['id'].ToString() -eq $removed}|Select-Object -First 1;$deletedBlob=Blob $deletedPoint 'g/mods/library.jar'
 $deletedFolder=Call $history 'DeletePoints' @($instance.PathVersion,[string[]]@($removed))
 for($i=6;$i -le 8;$i++){Put (Join-Path $version 'mods/library.jar') ('auto-'+$i);Capture '游戏启动前'|Out-Null}
 if([IO.File]::Exists($deletedBlob) -or [IO.File]::Exists((Join-Path $deletedFolder ('points/'+$removed+'.json'))) -or [IO.Directory]::Exists((Join-Path $deletedFolder 'deleted'))){throw 'Deleted restore data remains'}
 # Cancellation and invalid limits cannot trigger cleanup.
 $count=(Points).Count;$cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 Reject {Call $history 'CaptureInstance' @($instance,'游戏启动前',$cancel.Token)} 'Canceled capture succeeded'
 Reject {Call $history 'PruneAutomatic' @($instance.PathVersion,0,$latest,$none)} 'Invalid limit was accepted'
 if((Points).Count -ne $count){throw 'Failed capture or invalid limit removed a restore point'}
 # During restore the target and the safety snapshot must both survive, even at limit one.
 $target=(Points)|Where-Object {-not (Manual $_)}|Select-Object -Last 1;$targetId=$target['id'].ToString();$expected='auto-7'
 $settings['VersionEnvironmentHistoryRetention'].ValueCache[$instance.PathVersion]=1
 Put (Join-Path $version 'mods/library.jar') 'before-restore'
 $captureBefore=[Func[string]]{return [string](Capture '恢复前自动保存')}
 $undo=Call $history 'Restore' @($instance.PathVersion,$instance.PathIndie,$mc,$targetId,$none,$null,$captureBefore)
 if([IO.File]::ReadAllText((Join-Path $version 'mods/library.jar')) -cne $expected){throw 'Cleanup removed the selected target during restore'}
 Call $history 'ApplyRetention' @($instance,$undo,$none)|Out-Null
 if(@((Points)|Where-Object {-not (Manual $_)}).Count -ne 1 -or -not ((Points)|Where-Object {$_['id'].ToString() -eq $undo})){throw 'Post-restore cleanup discarded the undo snapshot'}
 Call $history 'Restore' @($instance.PathVersion,$instance.PathIndie,$mc,$undo,$none,$null,$captureBefore)|Out-Null
 if([IO.File]::ReadAllText((Join-Path $version 'mods/library.jar')) -cne 'before-restore'){throw 'Retained undo point is not restorable'}
 # Malformed references stop pruning before any deletion.
 $record=PointPath $manualOne;$valid=[IO.File]::ReadAllText($record);$bad=[Newtonsoft.Json.Linq.JObject]::Parse($valid);$bad['files']['g/mods/library.jar']=[Newtonsoft.Json.Linq.JValue]::new('sha1:invalid');[IO.File]::WriteAllText($record,$bad.ToString());$count=(Points).Count
 Reject {Call $history 'PruneAutomatic' @($instance.PathVersion,1,$undo,$none)} 'Malformed references were ignored during garbage collection'
 if((Points).Count -ne $count){throw 'Invalid references caused partial pruning'}
 [IO.File]::WriteAllText($record,$valid)
 # Settings are per instance, remain off by default, and never clean immediately when toggled.
 $page=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceRestorePoints'));Set-RestorePageState $page 'Owner' $instance
 $settings['VersionEnvironmentHistoryAutoCleanup'].ValueCache[$instance.PathVersion]=$false
 Call $page 'BindRetention' @()|Out-Null
 if($page.FindName('CheckAutoCleanup').Checked -or $page.FindName('PanRetention').Visibility.ToString() -ne 'Collapsed'){throw 'Cleanup opt-in UI is not collapsed when off'}
 $count=(Points).Count;$page.FindName('CheckAutoCleanup').Checked=$true
 Call $page 'AutoCleanupChanged' @($null,$true)|Out-Null
 if(-not $settings['VersionEnvironmentHistoryAutoCleanup'].ValueCache[$instance.PathVersion] -or $page.FindName('PanRetention').Visibility.ToString() -ne 'Visible' -or (Points).Count -ne $count){throw 'Opt-in did not persist or deleted points immediately'}
 $page.FindName('TextRetention').Text='3';Set-RestorePageState $page 'RetentionDirty' $true
 if(-not (Call $page 'CommitRetention' @()) -or $settings['VersionEnvironmentHistoryRetention'].ValueCache[$instance.PathVersion] -ne 3){throw 'Retention limit was not persisted for its instance'}
 $page.FindName('TextRetention').Text='0';Set-RestorePageState $page 'RetentionDirty' $true
 if((Call $page 'CommitRetention' @()) -or $page.FindName('TextRetention').Text -ne '3'){throw 'Invalid UI limit was persisted'}
 if($PreviewDirectory){$card=$page.FindName('CardRetention');$caption=$card.MainTextBlock;$card.Parent.Children.Remove($card);$card.Margin=[Windows.Thickness]::new(15);$card.VerticalAlignment=[Windows.VerticalAlignment]::Top;Render $card 'restore-point-retention.png' 740 175;$worldSettings=[Activator]::CreateInstance($asm.GetType('PCL.WorldBackupSettings'));Call $worldSettings 'BindOwner' @($instance)|Out-Null;$backupCard=$worldSettings.FindName('CardSettings');$caption=$backupCard.MainTextBlock;$backupCard.IsSwapped=$false;$backupCard.TriggerForceResize();$worldSettings.VerticalAlignment=[Windows.VerticalAlignment]::Top;Render $worldSettings 'manual-world-backup-protection.png' 740 155}
 Write-Output 'PASS: opt-in/per-instance limits, legacy manual exemption, newest-first automatic retention, shared-object cleanup, permanent deletion and shared-reference protection, cancellation, restore/undo, invalid metadata and native settings UI.'
}finally{
 $app.Shutdown();$history.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^PclBackupRetention-[a-f0-9]{32}$'){throw 'Unsafe retention fixture cleanup'}
 if([IO.Directory]::Exists($resolved)){[IO.Directory]::Delete($resolved,$true)}
}
