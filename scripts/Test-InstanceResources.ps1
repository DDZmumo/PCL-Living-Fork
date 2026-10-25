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
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-Resources-'+[Guid]::NewGuid().ToString('N'))
try{
 $worldKind=Kind 'Saves';$imageKind=Kind 'Screenshots';$packKind=Kind 'ResourcePacks';$shaderKind=Kind 'ShaderPacks';$schematicKind=Kind 'Schematics'
 $missing=Join-Path $fixture 'absent'
 if((Scan $missing $worldKind).Count -ne 0 -or [IO.Directory]::Exists($missing)){throw 'Viewing resources created a missing folder'}
 $mc=Join-Path $fixture '.minecraft'
 $saves=Invoke-Store 'Root' @($mc,$worldKind)
 Put (Join-Path $saves 'Test World/level.dat') 'synthetic-world-data'
 Put (Join-Path $saves 'Test World/region/r.0.0.mca') 'synthetic-region'
 Write-Png (Join-Path $saves 'Test World/icon.png')
 $worlds=Scan $saves $worldKind
 if($worlds.Count -ne 1 -or $worlds[0].Name -ne 'Test World'){throw 'World folders were not listed'}
 $export=Join-Path $fixture 'world.zip'
 Invoke-Store 'Export' @($saves,$worlds[0],$export,$none)|Out-Null
 $archive=[IO.Compression.ZipFile]::OpenRead($export)
 try{if(-not $archive.GetEntry('level.dat') -or -not $archive.GetEntry('region/r.0.0.mca')){throw 'World export omitted files'}}finally{$archive.Dispose()}
 Assert-Rejected {Invoke-Store 'Export' @($saves,$worlds[0],(Join-Path $worlds[0].FullPath 'self.zip'),$none)} 'World export recursively included itself'
 Invoke-Store 'ImportWorld' @($saves,$export,'Imported',$none)|Out-Null
 if([IO.File]::ReadAllText((Join-Path $saves 'Imported/level.dat')) -cne 'synthetic-world-data'){throw 'World ZIP round trip changed data'}
 Assert-Rejected {Invoke-Store 'ImportWorld' @($saves,$export,'Imported',$none)} 'World import overwrote an existing world'
 Invoke-Store 'ImportFolder' @($saves,$saves,$worldKind,(Join-Path $saves 'Imported'),'Folder Copy',$none)|Out-Null
 if(-not [IO.File]::Exists((Join-Path $saves 'Folder Copy/region/r.0.0.mca'))){throw 'World-folder import failed'}
 $bad=Join-Path $fixture 'traversal.zip';New-Zip $bad @{'../outside.txt'='must-not-write';'level.dat'='bad'}
 Assert-Rejected {Invoke-Store 'ImportWorld' @($saves,$bad,'Bad',$none)} 'ZIP traversal was accepted'
 if([IO.File]::Exists((Join-Path $fixture 'outside.txt')) -or [IO.Directory]::Exists((Join-Path $saves 'Bad'))){throw 'ZIP failure wrote outside its staging directory'}
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 try{Assert-Rejected {Invoke-Store 'ImportWorld' @($saves,$export,'Canceled',$cancel.Token)} 'Canceled import completed'}finally{$cancel.Dispose()}
 if(@([IO.Directory]::GetDirectories($saves,'.pcl-import-*')).Count){throw 'Canceled import left staging directories'}
 $packs=Invoke-Store 'Root' @($mc,$packKind);[IO.Directory]::CreateDirectory($packs)|Out-Null
 $pack=Join-Path $fixture 'Native Pack.zip';New-Zip $pack @{'pack.mcmeta'='{"pack":{"pack_format":46,"description":"Synthetic native pack"}}'}
 Invoke-Store 'ImportFile' @($packs,$packs,$packKind,$pack,$none)|Out-Null
 $packEntries=Scan $packs $packKind
 if($packEntries.Count -ne 1 -or -not $packEntries[0].Description.Contains('Synthetic native pack')){throw 'Resource-pack metadata was not read'}
 Assert-Rejected {Invoke-Store 'ImportFile' @($packs,$packs,$packKind,$pack,$none)} 'Resource import overwrote an existing file'
 $renamed=Invoke-Store 'Rename' @($packs,$packEntries[0],'Renamed.zip')
 if(-not [IO.File]::Exists($renamed)){throw 'Resource rename failed or retained an archive handle'}
 $trash=Join-Path $mc 'PCL/ResourceRecycle/ResourcePacks'
 $renamedEntry=(Scan $packs $packKind)[0]
 Invoke-Store 'MoveToRecycle' @($packs,$renamedEntry,$trash)|Out-Null
 if([IO.File]::Exists($renamed)){throw 'Deleted resource remained in the live folder'}
 $restored=Invoke-Store 'RestoreLast' @($packs,$trash)
 if(-not [IO.File]::Exists($restored) -or (Get-FileHash -LiteralPath $restored).Hash -ne (Get-FileHash -LiteralPath $pack).Hash){throw 'Restore did not preserve resource bytes'}
 $stale=(Scan $packs $packKind)[0];[IO.File]::AppendAllText($stale.FullPath,'changed-after-listing')
 Assert-Rejected {Invoke-Store 'MoveToRecycle' @($packs,$stale,$trash)} 'A changed resource was deleted through a stale selection'
 $shots=Invoke-Store 'Root' @($mc,$imageKind);Write-Png (Join-Path $shots 'screenshot.png')
 $images=Scan $shots $imageKind
 $preview=Invoke-Store 'LoadImage' @($images[0].FullPath,1200)
 if($null -eq $preview -or -not $preview.IsFrozen){throw 'Screenshot preview was not fully loaded and frozen'}
 $moved=Join-Path $shots 'renamed.png';[IO.File]::Move($images[0].FullPath,$moved);[IO.File]::Move($moved,$images[0].FullPath)
 $shaders=Invoke-Store 'Root' @($mc,$shaderKind);[IO.Directory]::CreateDirectory($shaders)|Out-Null
 Invoke-Store 'ImportFile' @($shaders,$shaders,$shaderKind,$pack,$none)|Out-Null
 $schematics=Invoke-Store 'Root' @($mc,$schematicKind);Put (Join-Path $schematics 'house.litematic') 'schematic-fixture';Put (Join-Path $schematics 'group/tree.schem') 'schematic-folder'
 if((Scan $shaders $shaderKind).Count -ne 1 -or (Scan $schematics $schematicKind).Count -ne 2){throw 'Shader or schematic resources were not listed'}
 Assert-Rejected {Invoke-Store 'EnsureWithin' @($schematics,(Join-Path $fixture 'foreign.txt'),$false)} 'Resource scope accepted an unrelated path'
 foreach($name in @('../escape','CON','file.','a/b')){Assert-Rejected {Invoke-Store 'SafeName' @($name)} 'Unsafe resource name was accepted'}
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0))|Out-Null
 $left=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceLeft'))
 $left.RefreshModDisabled()
 $pageType=$asm.GetType('PCL.PageInstanceResources')
 $subType=$asm.GetType('PCL.FormMain+PageSubType')
 foreach($index in 0..4){
  $id=[Enum]::ToObject($subType,6+$index)
  $nav=$left.GetType().GetMethod('NavigationItem',$flags).Invoke($left,@($id))
  if([int]$nav.Tag -ne 6+$index){throw 'Native sidebar navigation still depends on child positions'}
  $page=$left.PageGet($id)
  if($page.GetType() -ne $pageType -or $page.FindName('SearchBox').GetType().Name -ne 'MySearchBox' -or $page.FindName('CardList').GetType().Name -ne 'MyCard'){throw 'Resource page does not use native PCL controls'}
 }
 $packPage=$left.PageGet([Enum]::ToObject($subType,8))
 $items=Scan $packs $packKind
 $pageType.GetMethod('ShowResources',$flags).Invoke($packPage,@([string]$packs,[string]$packs,$items.PSObject.BaseObject))|Out-Null
 if($packPage.FindName('PanList').Children.Count -ne 1){throw 'Native resource cards were not rendered'}
 $packPage.FindName('SearchBox').Text='not-present'
 if($packPage.FindName('PanList').Children.Count -ne 0 -or $packPage.FindName('LabEmpty').Visibility -ne 'Visible'){throw 'Resource search or empty state failed'}
 $packPage.FindName('SearchBox').Text=''
 $second=Join-Path $packs 'Second.zip';[IO.File]::Copy($pack,$second)
 Put (Join-Path $packs 'Folder pack/pack.mcmeta') '{"pack":{"pack_format":46,"description":"Folder fixture"}}'
 $items=Scan $packs $packKind
 $pageType.GetMethod('ShowResources',$flags).Invoke($packPage,@([string]$packs,[string]$packs,$items.PSObject.BaseObject))|Out-Null
 if($packPage.FindName('CardSelected') -or $packPage.FindName('LabFolder')){throw 'Old separate details/path card remains'}
 foreach($name in @('BtnImportFolder','BtnCheckUpdates','BtnRefresh')){if($packPage.FindName($name)){throw 'Extra toolbar icons remain'}}
 if(@($packPage.FindName('BtnOpenFolder').Parent.Children|Where-Object {$_.Visibility -ne 'Collapsed'}).Count -ne 4){throw 'Resource toolbar differs from the native four-button layout'}
 if($packPage.FindName('BtnFilterAll').Text -notmatch '3' -or $packPage.FindName('BtnDownload').Visibility -ne 'Visible'){throw 'Native count filter or resource download action missing'}
 $rowType=$asm.GetType('PCL.MyLocalModItem')
 foreach($row in $packPage.FindName('PanList').Children){if($row.GetType() -ne $rowType -or $row.Height -ne 44 -or @($row.Buttons).Count -ne 4){throw 'Resource rows do not reuse the native mod item and hover actions'}}
 $nativeMod=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceMod'))
 foreach($pair in @(@('BtnUpdate','BtnSelectUpdate'),@('BtnDelete','BtnSelectDelete'),@('BtnDeselect','BtnSelectCancel'))){
  $actual=$packPage.FindName($pair[0]);$reference=$nativeMod.FindName($pair[1])
  if($actual.Logo -cne $reference.Logo -or $actual.LogoScale -ne $reference.LogoScale){throw 'Selection action icon differs from the original mod page'}
 }
 if($packPage.FindName('PanManageButtons').Height -ne 35 -or $packPage.FindName('PanManageButtons').Margin.ToString() -ne '15,15,0,15'){throw 'Toolbar spacing differs from the mod page'}
 $pageType.GetMethod('ChangeAllSelected',$flags).Invoke($packPage,@($true))|Out-Null
 if($packPage.FindName('CardSelect').Visibility -ne 'Visible' -or @($packPage.FindName('PanList').Children|Where-Object Checked).Count -ne 3 -or $packPage.FindName('TransSelect').Y -ne -25){throw 'Native bulk selection/action availability failed'}
 $pageType.GetMethod('ChangeAllSelected',$flags).Invoke($packPage,@($false))|Out-Null
 if($packPage.FindName('CardSelect').Visibility -ne 'Collapsed'){throw 'Cancel selection did not hide the native action bar'}
 $first=$packPage.FindName('PanList').Children[0];$first.Checked=$true
 if(-not $first.Checked -or @($first.Buttons).Count -ne 4){throw 'Single-selection hover actions unavailable'}
 $packPage.FindName('SearchBox').Text='not-present'
 if($packPage.FindName('CardSelect').Visibility -ne 'Collapsed'){throw 'Search retained hidden selected resources'}
 $packPage.FindName('SearchBox').Text=''
 $packPage.FindName('BtnFilterFolders').Checked=$true
 if($packPage.FindName('PanList').Children.Count -ne 1 -or -not $rowType.GetField('ResourceEntry',$flags).GetValue($packPage.FindName('PanList').Children[0]).IsDirectory){throw 'Folder resource filter failed'}
 $packPage.FindName('BtnFilterAll').Checked=$true
 $onlineType=$asm.GetType('PCL.InstanceResourceOnlineInfo');$online=[Activator]::CreateInstance($onlineType)
 $online.Entry=$items|Where-Object {-not $_.IsDirectory}|Select-Object -First 1
 $online.ProjectName='Native resource pack';$online.Status='Update fixture'
 $online.CurrentVersion=[Activator]::CreateInstance($asm.GetType('PCL.ResourceVersion'));$online.CurrentVersion.Version='1.0'
 $online.UpdateVersion=[Activator]::CreateInstance($asm.GetType('PCL.ResourceVersion'));$online.UpdateVersion.Version='2.0'
 $onlineList=[Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($onlineType));$onlineList.Add($online)
 $beforeRows=@($packPage.FindName('PanList').Children)
 $pageType.GetMethod('ApplyOnlineInfo',$flags).Invoke($packPage,[object[]](,$onlineList.PSObject.BaseObject))|Out-Null
 for($i=0;$i -lt $beforeRows.Count;$i++){if(-not [object]::ReferenceEquals($beforeRows[$i],$packPage.FindName('PanList').Children[$i])){throw 'Online metadata rebuilt an existing mod-style row'}}
 $packPage.FindName('BtnFilterUpdates').Checked=$true
 if($packPage.FindName('PanList').Children.Count -ne 1 -or $packPage.FindName('BtnFilterUpdates').Visibility -ne 'Visible'){throw 'Updatable resource filter failed'}
 $pageType.GetMethod('ChangeAllSelected',$flags).Invoke($packPage,@($true))|Out-Null
 if(-not $packPage.FindName('BtnUpdate').IsEnabled){throw 'Bulk update action unavailable for matching files'}
 $packPage.FindName('BtnFilterAll').Checked=$true
 $exportMany=Join-Path $fixture 'batch-export';[IO.Directory]::CreateDirectory($exportMany)|Out-Null
 Invoke-Store 'ExportMany' @($packs,$items.PSObject.BaseObject,$exportMany,$none)|Out-Null
 if(@([IO.Directory]::EnumerateFiles($exportMany)).Count -ne 3){throw 'Batch export did not include files and zipped folders'}
 Assert-Rejected {Invoke-Store 'ExportMany' @($packs,$items.PSObject.BaseObject,$exportMany,$none)} 'Batch export overwrote an existing destination'
 $recycleEntries=Scan $packs $packKind
 Invoke-Store 'RecycleMany' @($packs,$recycleEntries.PSObject.BaseObject,$trash,$none)|Out-Null
 if((Scan $packs $packKind).Count -ne 0){throw 'Batch deletion left selected resources live'}
 foreach($item in $recycleEntries){Invoke-Store 'RestoreLast' @($packs,$trash)|Out-Null}
 $items=Scan $packs $packKind
 $pageType.GetMethod('ShowResources',$flags).Invoke($packPage,@([string]$packs,[string]$packs,$items.PSObject.BaseObject))|Out-Null
 $emptyPage=$left.PageGet([Enum]::ToObject($subType,7))
 $emptyItems=[Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($asm.GetType('PCL.InstanceResourceEntry')))
 $pageType.GetMethod('ShowResources',$flags).Invoke($emptyPage,@([string]$shots,[string]$shots,$emptyItems.PSObject.BaseObject))|Out-Null
 if($emptyPage.FindName('PanEmpty').Visibility -ne 'Visible' -or $emptyPage.FindName('PanBack').Visibility -ne 'Collapsed'){throw 'Empty resources do not use the native centered empty state'}
 if($PreviewDirectory){
  $nav=$left.GetType().GetMethod('NavigationItem',$flags).Invoke($left,@([Enum]::ToObject($subType,8)))
  $savedTag=$nav.Tag;$nav.Tag=$null;$nav.SetChecked($true,$false,$false);$nav.Tag=$savedTag
  [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory))|Out-Null
  $panel=[Windows.Controls.Grid]::new();$panel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#E7EFFB')
  $panel.ColumnDefinitions.Add([Windows.Controls.ColumnDefinition]::new());$panel.ColumnDefinitions[0].Width=[Windows.GridLength]::new(190)
  $panel.ColumnDefinitions.Add([Windows.Controls.ColumnDefinition]::new())
  $sidebar=$left.Children[0];$left.Children.Remove($sidebar)
  $sidebar.Background=[Windows.Media.Brushes]::White
  $panel.Children.Add($sidebar)|Out-Null
  $packPage.FindName('PanBack').Visibility='Visible';$packPage.FindName('PanLoad').Visibility='Collapsed'
  foreach($name in @('BtnUpdate','BtnExport','BtnDelete','BtnDeselect')){
   $button=$packPage.FindName($name);$button.GetType().GetMethod('RefreshColor',$flags).Invoke($button,@($null,$false))|Out-Null
  }
  $packPage.FindName('PanAllBack').Visibility='Visible'
  $content=$packPage.Child;$packPage.Child=$null
  [Windows.Controls.Grid]::SetColumn($content,1);$panel.Children.Add($content)|Out-Null
  Render-Png $panel (Join-Path $PreviewDirectory 'native-instance-resources.png') 1080 710
  $panel.Children.Clear()
 }
 Write-Output 'Instance resources passed: five native pages, compact checkbox rows/hover actions, native filters/action bar, single/bulk selection, hidden-selection clearing, online update filter, bulk export/recycle/restore, world round trips, metadata/preview handle release, import collision/cancellation and path/stale-entry protection.'
}finally{
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-Resources-[a-f0-9]{32}\z'){throw 'Unsafe resource fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
 $app.Shutdown()
}
