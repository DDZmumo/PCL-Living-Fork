param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Instantiate the real controls offscreen. Only GUID temporary instance/profile files are written.
# No launcher entry point, network, external viewer, game process, restore, or deletion task is started.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclWorldBackupGroups-'+[Guid]::NewGuid().ToString('N'))
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))}
function Invoke-Control($control,[string]$method,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){if($arguments[$i] -is [Newtonsoft.Json.Linq.JToken]){$values[$i]=$arguments[$i]}else{$values[$i]=$arguments[$i].PSObject.BaseObject}}}
 try{return ,$control.GetType().GetMethod($method,$flags).Invoke($control,$values)}catch{throw ($method+': '+$_.Exception.ToString())}
}
function Selected($group){return ,$group.GetType().GetProperty('SelectedIds',$flags).GetValue($group,$null)}
function New-Point([string]$world,[string]$id,[int]$days){
 return ,([Newtonsoft.Json.Linq.JObject]::FromObject(@{id=$id;world=$world;label='Same display name';created=[DateTime]::UtcNow.AddDays(-$days);reason='manual';description=($world+' - manual - 19 B');healthy=$false;bytes=[long]19;archiveBytes=[long]120;files=@{}}))
}
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null
 $app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
 $app.InitializeComponent()
 $app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
 $app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
 [Windows.Application]::ResourceAssembly=$asm
 $entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
 foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
 $entries['Identify'].ValueCache['']='world-backup-group-fixture'
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0))|Out-Null
 $local=$asm.GetType('PCL.LocalGameData',$true);$local.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'profile'),$null)
 $engine=$asm.GetType('PCL.WorldBackups',$true);$engine.GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'backups'),$null)
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance',$true)
 $game=Join-Path $fixture 'minecraft/versions/fixture';$anotherGame=Join-Path $fixture 'other/versions/fixture'
 foreach($folder in @($game,$anotherGame)){
  Put (Join-Path $folder 'fixture.json') '{"id":"fixture","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{},"arguments":{"game":[],"jvm":[]}}'
  Put (Join-Path $folder 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
  Put (Join-Path $folder 'saves/Folder A/level.dat') 'fixture-world-a'
 }
 Put (Join-Path $game 'saves/Folder B/level.dat') 'fixture-world-b'
 $owner=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$game))
 $otherOwner=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$anotherGame))
 foreach($instance in @($owner,$otherOwner)){
  foreach($entry in $entries.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$instance.PathVersion]=$entry.DefaultValue}
  $entries['VersionArgumentIndieV2'].ValueCache[$instance.PathVersion]=$true
 }
 $beforeSetup=[IO.File]::ReadAllText((Join-Path $game 'PCL/Setup.ini'))
 $beforeOther=[IO.File]::ReadAllText((Join-Path $anotherGame 'PCL/Setup.ini'))
 $groupType=$asm.GetType('PCL.WorldBackupGroup',$true);$settingsType=$asm.GetType('PCL.WorldBackupSettings',$true)
 $ids=@([Guid]::NewGuid().ToString('N'),[Guid]::NewGuid().ToString('N'),[Guid]::NewGuid().ToString('N'))
 $points=[Collections.Generic.List[Newtonsoft.Json.Linq.JObject]]::new()
 $points.Add((New-Point 'Folder A' $ids[0] 0));$points.Add((New-Point 'Folder A' $ids[1] 1));$points.Add((New-Point 'Folder B' $ids[2] 0))
 $group=$groupType.GetConstructor([type[]]@()).Invoke(@())
 $bar=$asm.GetType('PCL.MyCard').GetConstructor([type[]]@()).Invoke(@())
 $groupType.GetProperty('SelectionBar',$flags).SetValue($group,$bar,$null)
 Invoke-Control $group 'Bind' @($owner,'Folder A',$points)|Out-Null
 $card=$group.FindName('CardBackups');$rows=$group.FindName('PanEntries')
 if(-not $card.CanSwap -or -not $card.IsSwapped -or $card.SwapControl -ne $group.FindName('PanContent') -or $card.Title -ne '备份（2）' -or $rows.Children.Count -ne 0){throw 'Backup group does not use a collapsed native MyCard with lazy rows'}
 if($groupType.GetProperty('FolderName',$flags).GetValue($group,$null) -cne 'Folder A' -or -not $group.FindName('BtnCreate').IsEnabled){throw 'World folder identity or per-world create action unavailable'}
 $card.IsSwapped=$false
 if($rows.Children.Count -ne 2 -or @($rows.Children|Where-Object {$_.GetType().FullName -ne 'PCL.MyLocalModItem'}).Count){throw 'Expanded backups are not native MyLocalModItem rows'}
 foreach($row in $rows.Children){
  if($row.GetType().GetField('SelectionBar',$flags).GetValue($row) -ne $bar){throw 'Backup row did not receive the parent selection bar'}
  $record=$row.GetType().GetField('RestorePoint',$flags).GetValue($row)
  if($record['world'].ToString() -cne 'Folder A' -or $row.Buttons.Count -ne 4){throw 'Display-name collision mixed worlds or row actions are missing'}
 }
 $script:selectionEvents=0
 $event=$groupType.GetEvent('SelectionChanged',$flags)
 $handler=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($sender) $script:selectionEvents++},$event.EventHandlerType)
 $event.GetAddMethod($true).Invoke($group,@($handler))|Out-Null
 $rows.Children[0].Checked=$true
 if((Selected $group).Count -ne 1 -or $script:selectionEvents -ne 1){throw 'Backup selection did not notify the parent'}
 Invoke-Control $group 'ClearSelection' @($false)|Out-Null
 if((Selected $group).Count -ne 0 -or $rows.Children[0].Checked -or $script:selectionEvents -ne 1){throw 'Silent deselection was not silent or did not clear rows'}
 $rows.Children[0].Checked=$true
 $selectedSnapshot=Selected $group
 Invoke-Control $group 'ClearSelection' @($true)|Out-Null
 if((Selected $group).Count -ne 0 -or $selectedSnapshot.Count -ne 1 -or $script:selectionEvents -ne 3){throw 'Public selected IDs are mutable state or notified deselection failed'}
 # Exercise the same state handler that the native MyCard Swap event calls.
 Invoke-Control $group 'RememberExpansion' @()|Out-Null
 $rebound=$groupType.GetConstructor([type[]]@()).Invoke(@())
 Invoke-Control $rebound 'Bind' @($owner,'Folder A',$points)|Out-Null
 if($rebound.FindName('CardBackups').IsSwapped -or $rebound.FindName('PanEntries').Children.Count -ne 2){throw 'Expansion was not retained across group rebuilds'}
 $independent=$groupType.GetConstructor([type[]]@()).Invoke(@())
 Invoke-Control $independent 'Bind' @($otherOwner,'Folder A',$points)|Out-Null
 if(-not $independent.FindName('CardBackups').IsSwapped){throw 'Expansion state leaked to a different game directory'}
 $worldB=$groupType.GetConstructor([type[]]@()).Invoke(@())
 Invoke-Control $worldB 'Bind' @($owner,'Folder B',$points)|Out-Null
 if(-not $worldB.FindName('CardBackups').IsSwapped -or $worldB.FindName('CardBackups').Title -ne '备份（1）'){throw 'Expansion or grouping leaked between same-name worlds'}
 $ghostPoints=[Collections.Generic.List[Newtonsoft.Json.Linq.JObject]]::new();$ghostPoints.Add((New-Point 'Deleted folder' ([Guid]::NewGuid().ToString('N')) 0))
 $ghost=$groupType.GetConstructor([type[]]@()).Invoke(@())
 Invoke-Control $ghost 'Bind' @($owner,'Deleted folder',$ghostPoints)|Out-Null
 if($ghost.FindName('BtnCreate').IsEnabled -or -not $ghost.FindName('LabState').Text.Contains('已不存在')){throw 'Backup-only world can create a new snapshot or lacks its missing-world state'}
 $ghost.FindName('CardBackups').IsSwapped=$false
 if($ghost.FindName('PanEntries').Children.Count -ne 1 -or $ghost.FindName('PanEntries').Children[0].Buttons.Count -ne 4){throw 'Deleted-world backups lost restore actions'}
 # Binding settings and programmatic UI changes must not write to either instance.
 $settings=$settingsType.GetConstructor([type[]]@()).Invoke(@())
 Invoke-Control $settings 'BindOwner' @($owner)|Out-Null
 if(-not $settings.FindName('CardSettings').CanSwap -or -not $settings.FindName('CardSettings').IsSwapped -or $settings.FindName('CardSettings').SwapControl -ne $settings.FindName('PanSettings')){throw 'Backup settings do not use a collapsed native MyCard'}
 if(-not $settings.IsEnabled -or -not $settings.FindName('CheckAuto').Checked -or $settings.FindName('TextRetention').Text -ne '5'){throw 'Backup settings did not bind instance defaults'}
 $settings.FindName('TextRetention').Text='9'
 Invoke-Control $settings 'CommitPending' @()|Out-Null
 $settings.FindName('CheckAuto').Checked=$false
 Invoke-Control $settings 'AutoChanged' @($settings.FindName('CheckAuto'),$false)|Out-Null
 if($entries['VersionWorldBackupRetention'].ValueCache[$owner.PathVersion] -ne 5 -or -not $entries['VersionWorldBackupAutoEnabled'].ValueCache[$owner.PathVersion]){throw 'Programmatic settings changes wrote instance settings'}
 Invoke-Control $settings 'BindOwner' @($otherOwner)|Out-Null
 Invoke-Control $settings 'BindOwner' @($owner)|Out-Null
 if([IO.File]::ReadAllText((Join-Path $game 'PCL/Setup.ini')) -cne $beforeSetup -or [IO.File]::ReadAllText((Join-Path $anotherGame 'PCL/Setup.ini')) -cne $beforeOther){throw 'Binding wrote an instance settings file'}
 # Offscreen controls cannot take keyboard focus: mark the same dirty flag that actual focused text edits set.
 $settings.FindName('TextRetention').Text='7';$settingsType.GetField('RetentionDirty',$flags).SetValue($settings,$true)
 if(-not (Invoke-Control $settings 'CommitPending' @()) -or $entries['VersionWorldBackupRetention'].ValueCache[$owner.PathVersion] -ne 7 -or $entries['VersionWorldBackupRetention'].ValueCache[$otherOwner.PathVersion] -ne 5){throw 'User retention edit did not remain scoped to its owner'}
 $settings.FindName('TextRetention').Text='51';$settingsType.GetField('RetentionDirty',$flags).SetValue($settings,$true)
 if((Invoke-Control $settings 'CommitPending' @()) -or $settings.FindName('TextRetention').Text -ne '7' -or $entries['VersionWorldBackupRetention'].ValueCache[$owner.PathVersion] -ne 7){throw 'Invalid retention was persisted'}
 $settings.FindName('CheckAuto').Checked=$false
 Invoke-Control $settings 'AutoChanged' @($settings.FindName('CheckAuto'),$true)|Out-Null
 if($entries['VersionWorldBackupAutoEnabled'].ValueCache[$owner.PathVersion] -or -not $entries['VersionWorldBackupAutoEnabled'].ValueCache[$otherOwner.PathVersion]){throw 'User automatic-backup switch affected another owner'}
 Invoke-Control $settings 'BindOwner' @($null)|Out-Null
 if($settings.IsEnabled){throw 'Unbound settings remain interactive'}
 $parameters=$asm.GetType('PCL.WorldBackupsUi',$true).GetMethod('Start',$flags).GetParameters()
 if($parameters[-1].Name -ne 'WorldIdentifier' -or -not $parameters[-1].IsOptional){throw 'Per-world manual backup API is missing or incompatible'}
 $source=[IO.File]::ReadAllText((Join-Path $root 'Plain Craft Launcher 2/Pages/PageInstance/WorldBackupGroup.xaml.vb'))
 $markup=[xml][IO.File]::ReadAllText((Join-Path $root 'Plain Craft Launcher 2/Pages/PageInstance/WorldBackupGroup.xaml'))
 if($source -match '\.Tag\s*=' -or -not $source.Contains('Row.BindRestorePoint(Point)') -or -not $source.Contains('WorldIdentifier:=Identifier') -or $markup.SelectSingleNode('//*[@*[local-name()="Name" and .="CardSelect"]]')){throw 'Group does not reuse native records and parent selection'}
 Write-Output 'World backup groups passed: real native controls, default collapse and lazy rows, per-folder grouping despite equal world names, shared selection bar and notifications, immutable selection snapshots, remembered expansion across refresh, independent folders/directories, backup-only world actions, read-only settings binding, user-only setting commits, range validation, owner isolation and optional per-world backup API. No game, launcher entry point, network, external viewer, backup, restore or delete task was started.'
}finally{
 if($engine){$engine.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)}
 if($local){$local.GetProperty('TestRoot',$flags).SetValue($null,$null,$null)}
 if($app){$app.Shutdown()}
 $resolved=[IO.Path]::GetFullPath($fixture);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^PclWorldBackupGroups-[a-f0-9]{32}$'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
