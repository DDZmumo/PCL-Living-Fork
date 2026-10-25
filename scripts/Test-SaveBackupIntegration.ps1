param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$PreviewDirectory)
# Offscreen PCL controls and GUID temporary files only; never start the launcher or touch game directories.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
$app.InitializeComponent()
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$app.remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_Startup',$flags)))
$app.remove_DispatcherUnhandledException([Delegate]::CreateDelegate([Windows.Threading.DispatcherUnhandledExceptionEventHandler],$app,$asm.GetType('PCL.Application').GetMethod('Application_DispatcherUnhandledException',$flags)))
[Windows.Application]::ResourceAssembly=$asm
$entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
$entries['Identify'].ValueCache['']='synthetic-resource-view'
$store=$asm.GetType('PCL.InstanceResourceStore',$true)
$kindType=$asm.GetType('PCL.InstanceResourceKind',$true)
$none=[Threading.CancellationToken]::None
function Invoke-Store($name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return ,$store.GetMethod($name,$flags).Invoke($null,$values)
}
function Kind([string]$name){return [Enum]::Parse($kindType,$name)}
function Put([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text)}
function Assert-Rejected([scriptblock]$action,[string]$message){$rejected=$false;try{& $action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $message}}
function New-Zip([string]$path,[hashtable]$items){
 $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try{foreach($name in $items.Keys){$writer=[IO.StreamWriter]::new($archive.CreateEntry($name).Open());try{$writer.Write($items[$name])}finally{$writer.Dispose()}}}finally{$archive.Dispose()}
}
function Write-Png([string]$path){
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
 $visual=[Windows.Media.DrawingVisual]::new();$drawing=$visual.RenderOpen()
 $drawing.DrawRectangle([Windows.Media.Brushes]::CornflowerBlue,$null,[Windows.Rect]::new(0,0,96,64));$drawing.Close()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(96,64,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($visual)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create($path);try{$encoder.Save($stream)}finally{$stream.Dispose()}
}
function Scan([string]$folder,$kind){return ,(Invoke-Store 'Scan' @($folder,$folder,$kind,$none))}
function Render-Png($element,[string]$path,[int]$width,[int]$height){
 $element.Width=$width;$element.Height=$height
 $element.Measure([Windows.Size]::new($width,$height));$element.Arrange([Windows.Rect]::new(0,0,$width,$height));$element.UpdateLayout()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($element)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create($path);try{$encoder.Save($stream)}finally{$stream.Dispose()}
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclSaveBackupUi-'+[Guid]::NewGuid().ToString('N'))
try {
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0))|Out-Null
 $asm.GetType('PCL.LocalGameData').GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'profile'),$null)
 $asm.GetType('PCL.WorldBackups').GetProperty('TestRoot',$flags).SetValue($null,(Join-Path $fixture 'backups'),$null)
 $game=Join-Path $fixture 'minecraft/versions/example';$saves=Join-Path $game 'saves'
 Put (Join-Path $game 'example.json') '{"id":"example","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}'
 Put (Join-Path $game 'PCL/Setup.ini') 'VersionArgumentIndieV2:True'
 foreach($name in @('Folder A','Folder B')){Put (Join-Path $saves ($name+'/level.dat')) 'synthetic-world'}
 $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance');$owner=$instanceType.GetConstructor([type[]]@([string])).Invoke(@([string]$game))
 foreach($entry in $entries.Values|Where-Object {$_.Source.ToString() -eq 'Instance'}){$entry.ValueCache[$owner.PathVersion]=$entry.DefaultValue}
 $entries['VersionArgumentIndieV2'].ValueCache[$owner.PathVersion]=$true
 $points=[Collections.Generic.List[Newtonsoft.Json.Linq.JObject]]::new()
 foreach($name in @('Folder A','Folder B','Removed Folder')){
  $json=@{id=[Guid]::NewGuid().ToString('N');world=$name;label='Same world display name';created=[datetime]::UtcNow;reason='manual';description='Fixture backup';healthy=$false;bytes=10;archiveBytes=8;files=@{}}
  $points.Add([Newtonsoft.Json.Linq.JObject]::Parse(($json|ConvertTo-Json -Depth 5)))
 }
 $pageType=$asm.GetType('PCL.PageInstanceResources');$page=$pageType.GetConstructor($flags,$null,[type[]]@($kindType),$null).Invoke(@((Kind 'Saves')))
 $pageType.GetField('Owner',$flags).SetValue($page,$owner.PSObject.BaseObject);$pageType.GetField('WorldBackupPoints',$flags).SetValue($page,$points)
 $items=Scan $saves (Kind 'Saves');$pageType.GetMethod('ShowResources',$flags).Invoke($page,[object[]]@([string]$saves,[string]$saves,$items.PSObject.BaseObject))|Out-Null
 $groups=$pageType.GetField('BackupGroups',$flags).GetValue($page)
 if($groups.Count -ne 3 -or $page.FindName('PanWorlds').Children.Count -ne 3){throw 'World and orphan-backup groups were not integrated'}
 $group=$groups['Folder A'];$card=$group.FindName('CardBackups');$card.IsSwapped=$false;$card.BuildContent()
 $backupRow=$group.FindName('PanEntries').Children[0];$backupRow.Checked=$true
 if($page.FindName('BtnBackupRestore').Visibility -ne 'Visible' -or -not $page.FindName('BtnBackupRestore').IsEnabled -or $page.FindName('BtnExport').Visibility -ne 'Collapsed'){throw 'Backup selection did not route the shared footer to backup actions'}
 if($pageType.GetField('SelectedPaths',$flags).GetValue($page).Count -ne 0){throw 'World and backup selections were mixed'}
 $worldHost=$page.FindName('PanWorlds').Children|Where-Object {$_ -is [Windows.Controls.StackPanel]}|Select-Object -First 1
 $worldRow=$worldHost.Children|Where-Object {$_.GetType().Name -eq 'MyLocalModItem'}|Select-Object -First 1
 $worldRow.Checked=$true
 if($group.GetType().GetProperty('SelectedIds',$flags).GetValue($group,$null).Count -ne 0 -or $page.FindName('BtnBackupRestore').Visibility -ne 'Collapsed' -or $page.FindName('BtnExport').Visibility -ne 'Visible'){throw 'World selection retained backup deletion targets'}
 $pageType.GetMethod('ChangeAllSelected',$flags).Invoke($page,@($true))|Out-Null
 if($pageType.GetField('SelectedPaths',$flags).GetValue($page).Count -ne 2){throw 'Select all included group containers or backup records'}
 $page.FindName('SearchBox').Text='Removed Folder'
 if($page.FindName('PanWorlds').Children.Count -ne 1 -or $page.FindName('PanEmpty').Visibility -ne 'Collapsed'){throw 'Backup-only worlds became inaccessible'}
 $orphan=$groups['Removed Folder'];if($orphan.FindName('BtnCreate').IsEnabled -or -not $orphan.FindName('CardBackups').Title.Contains('Removed Folder')){throw 'Orphan group is not clearly identified or permits backing up a missing world'}
 $page.FindName('SearchBox').Text=''
 $left=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceLeft'));if($left.FindName('ItemBackups')){throw 'Separate backup sidebar still exists'}
 $enum=$asm.GetType('PCL.FormMain+PageSubType');$legacy=[Enum]::Parse($enum,'InstanceBackups');$target=$left.PageGet($legacy)
 if($target.GetType() -ne $pageType -or $left.GetType().GetMethod('NavigationItem',$flags).Invoke($left,@($legacy)).Title -cne '存档'){throw 'Legacy backup navigation no longer resolves to saves'}
 $page.FindName('PanLoad').Visibility='Collapsed';$page.FindName('PanAllBack').Visibility='Visible';$page.FindName('PanAllBack').Opacity=1
 $group=$groups['Folder A'];$group.FindName('CardBackups').IsSwapped=$false;$group.FindName('CardBackups').BuildContent()
 foreach($worldGroup in $groups.Values){$null=$worldGroup.FindName('CardBackups').MainTextBlock}
 $null=$page.FindName('BackupSettings').FindName('CardSettings').MainTextBlock
 $panel=[Windows.Controls.Grid]::new();$panel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#e4effa');$panel.Children.Add($page)|Out-Null
 if($PreviewDirectory){[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory))|Out-Null;Render-Png $panel (Join-Path $PreviewDirectory 'save-backup-groups.png') 780 760}
 Write-Output 'Save/backup integration passed: per-folder and orphan grouping, native fold controls, exclusive destructive-action targets, bulk world selection, search, settings and legacy navigation.'
}finally{
 $app.Shutdown();$full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclSaveBackupUi-') -and [IO.Directory]::Exists($full)){Remove-Item -LiteralPath $full -Recurse -Force}
}
