param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$PreviewDirectory)
# Synthetic settings and GUID temporary files only. No launcher startup or real game access.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){
 [Reflection.Assembly]::LoadFrom((Join-Path $root $path)) | Out-Null
}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
$app.InitializeComponent()
[Windows.Application]::ResourceAssembly=$asm
$entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
$entries['Identify'].ValueCache['']='synthetic-modpack-mode'
$mode=$asm.GetType('PCL.ModModpackMode',$true)
$flags=[Reflection.BindingFlags]'NonPublic,Public,Static'
function Call-Mode($name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $arguments.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return $mode.GetMethod($name,$flags).Invoke($null,$values)
}
Add-Type -ReferencedAssemblies System.Core,System.IO.Compression,System.IO.Compression.FileSystem -TypeDefinition @'
using System;
using System.Reflection;
using System.IO;
using System.IO.Compression;
public static class ModpackConfirmationChecks {
 public static void WriteMod(string path, string id, string version) {
  using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))
  using(var writer=new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
   writer.Write("{\"id\":\""+id+"\",\"version\":\""+version+"\"}");
 }
 public static void CheckModWarnings(Type mode, bool enabled) {
  var method=mode.GetMethod("ConfirmModAction",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
  foreach(string action in new[]{"update","delete","install"}) foreach(bool accept in new[]{false,true}) {
   int calls=0;
   Func<string,bool> confirm=name=>{calls++;if(name!=action)throw new Exception("Wrong mod warning");return accept;};
   bool result=(bool)method.Invoke(null,new object[]{action,confirm});
   if(result!=(enabled ? accept : true) || calls!=(enabled ? 1 : 0))throw new Exception("Mod warning/cancellation failed");
  }
 }
 public static void Run(Type mode) {
  var method=mode.GetMethod("ConfirmChange",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
  foreach(bool current in new[]{false,true}) foreach(bool accepted in new[]{false,true}) {
   int calls=0;
   Func<bool,bool> confirm=desired=>{calls++;if(desired==current)throw new Exception("Wrong warning direction");return accepted;};
   bool result=(bool)method.Invoke(null,new object[]{current,!current,confirm});
   if(calls!=1 || result!=(accepted ? !current : current))throw new Exception("Confirmation/cancellation failed");
  }
  Func<bool,bool> unused=desired=>{throw new Exception("Unchanged state prompted");};
  if((bool)method.Invoke(null,new object[]{false,false,unused}))throw new Exception("Unchanged state mutated");
 }
}
'@
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-ModpackMode-'+[Guid]::NewGuid().ToString('N'))
$entry=$entries['LivingModpackMode']
try {
 $marker=$entries['LivingModpackExported']
 if($marker.Source.ToString() -ne 'Normal' -or $marker.DefaultValue -ne $false){throw 'Export marker must default off in local Setup.ini'}
 $entry.ValueCache['']=$true
 if($mode.GetProperty('Enabled',$flags).GetValue($null)){throw 'Unmarked launcher enabled modpack mode through a saved switch'}
 $entry.ValueCache['']=$false
 $marker.ValueCache['']=$true
 [ModpackConfirmationChecks]::Run($mode)
 if($entry.Source.ToString() -ne 'Normal' -or $entry.DefaultValue -ne $false){throw 'Mode must default off in local Setup.ini'}
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0)) | Out-Null
 $globals=$asm.GetType('PCL.ModMain')
 $left=[Activator]::CreateInstance($asm.GetType('PCL.PageSetupLeft'))
 $launch=[Activator]::CreateInstance($asm.GetType('PCL.PageLaunchLeft'))
 $globals.GetField('FrmSetupLeft').SetValue($null,$left)
 $globals.GetField('FrmLaunchLeft').SetValue($null,$launch)
 $animationField=$asm.GetType('PCL.ModAnimation').GetField('_AniControlEnabled',[Reflection.BindingFlags]'NonPublic,Static')
 $previousAnimation=$animationField.GetValue($null)
 $animationField.SetValue($null,($previousAnimation+1))
 $instanceViews=@{}
 try{
  foreach($name in @('PageInstanceLeft','PageInstanceOverall','PageInstanceMod','PageInstanceModDisabled','PageInstanceSetup','PageSetupLaunch')){
   $view=[Activator]::CreateInstance($asm.GetType('PCL.'+$name))
   $instanceViews[$name]=$view
   $globals.GetField(('Frm'+$name.Substring(4))).SetValue($null,$view)
  }
 }finally{$animationField.SetValue($null,$previousAnimation)}
 $overview=$instanceViews['PageInstanceOverall']
 $fakeHeader=[Activator]::CreateInstance($asm.GetType('PCL.MyListItem'))
 $fakeHeader.Title='26.2-Fabric 0.19.3';$fakeHeader.Info='synthetic header details';$fakeHeader.Height=42
 $fakeHeader.Logo='pack://application:,,,/LivingPCL;component/Images/Blocks/Fabric.png'
 $overview.GetType().GetField('InstanceHeaderItem').SetValue($overview,$fakeHeader)
 $overview.GetType().GetField('OriginalHeaderInfo',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($overview,'synthetic header details')
 $overview.FindName('PanDisplayItem').Children.Add($fakeHeader) | Out-Null
 $header=[Windows.Controls.StackPanel]::new()
 $buttons=@(0..4 | ForEach-Object {[Activator]::CreateInstance($asm.GetType('PCL.MyRadioButton'))})
 foreach($button in $buttons){$header.Children.Add($button) | Out-Null}
 $preferences=@{}
 foreach($key in $entries.Keys | Where-Object {$_ -like 'UiHidden*'}){$preferences[$key]=$entries[$key].ValueCache['']}
 foreach($force in @($false,$true)){
  $asm.GetType('PCL.PageSetupUI').GetField('_HiddenForceShow',$flags).SetValue($null,$force)
  $entry.ValueCache['']=$true
  [ModpackConfirmationChecks]::CheckModWarnings($mode,$true)
  if(-not (Call-Mode 'CanChangeMinecraft' @('26.2','26.2')) -or (Call-Mode 'CanChangeMinecraft' @('26.2','26.1'))){throw 'Minecraft version lock failed'}
  $suffix=' - '+[string][char]0x6574+[char]0x5408+[char]0x5305+[char]0x6a21+[char]0x5f0f
  if((Call-Mode 'DisplayTitle' @('LivingPCL')) -cne ('LivingPCL'+$suffix) -or (Call-Mode 'DisplayTitle' @('Custom')) -cne ('Custom'+$suffix)){throw 'Mode title suffix failed'}
  Call-Mode 'ApplyVisibility' @(,$null) | Out-Null
  Call-Mode 'ApplyHeader' @($header,$buttons[1],$buttons[2],$buttons[3],$buttons[4]) | Out-Null
  if($buttons[1].Visibility -ne 'Collapsed' -or $buttons[4].Visibility -ne 'Collapsed' -or $buttons[3].Visibility -ne 'Visible' -or $header.Visibility -ne 'Visible'){throw 'Compact top navigation failed'}
  if($left.FindName('ItemUI').Visibility -ne 'Collapsed' -or $left.FindName('ItemLink').Visibility -ne 'Collapsed' -or $left.FindName('ItemLaunch').Visibility -ne 'Visible' -or $left.FindName('ItemSystem').Visibility -ne 'Visible'){throw 'Compact setup navigation failed'}
  if($launch.FindName('BtnVersion').Visibility -ne 'Visible'){throw 'Version selection is missing in pack mode'}
  if([Windows.Controls.Grid]::GetColumnSpan($launch.FindName('BtnMore')) -ne 1){throw 'Version settings overlap the restored version selector'}
  if($overview.FindName('BtnManageDelete').Visibility -ne 'Collapsed' -or $overview.FindName('BtnDisplayStar').Visibility -ne 'Collapsed' -or $fakeHeader.Info -ne ''){throw 'Version deletion/favorite/header details remained visible'}
  foreach($logo in $fakeHeader.Children | Where-Object {$_ -is [Windows.Controls.Canvas] -and $_.Background -is [Windows.Media.ImageBrush]}){if($logo.Background.Stretch -ne 'Uniform'){throw 'Header bitmap is stretched'}}
  if($instanceViews['PageInstanceLeft'].FindName('ItemExport').Visibility -ne 'Collapsed' -or $instanceViews['PageInstanceLeft'].FindName('ItemModify').Visibility -ne 'Visible'){throw 'Export/modify navigation is incorrect'}
  $modifyId=[Enum]::ToObject($asm.GetType('PCL.FormMain+PageSubType'),2)
  $exportId=[Enum]::ToObject($asm.GetType('PCL.FormMain+PageSubType'),5)
  if([int](Call-Mode 'NormalizeInstancePage' @($modifyId)) -ne 2 -or [int](Call-Mode 'NormalizeInstancePage' @($exportId)) -ne 0){throw 'Version routing must preserve modify and reject export'}
  foreach($name in @('BtnManageDownload','BtnHintDownload')){if($instanceViews['PageInstanceMod'].FindName($name).Visibility -ne 'Collapsed'){throw 'Mod download entry remained visible'}}
  foreach($name in @('PageInstanceSetup','PageSetupLaunch')){
   $combo=$instanceViews[$name].FindName('ComboArgumentIndieV2')
   if($combo.Visibility -ne 'Visible' -or -not $combo.IsEnabled -or $instanceViews[$name].FindName('LabArgumentIndie').Visibility -ne 'Visible' -or $combo.Parent.RowDefinitions[0].Height.Value -le 0){throw 'Isolation setting must remain visible and editable in modpack mode'}
  }
  foreach($name in @('BtnManageDelete_Click')){$overview.GetType().GetMethod($name,[Reflection.BindingFlags]'NonPublic,Instance').Invoke($overview,@($null,$null)) | Out-Null}
  foreach($id in @(1,2,4)){
   $pageId=[Enum]::ToObject($asm.GetType('PCL.FormMain+PageType'),$id)
   if(-not (Call-Mode 'BlocksPage' @($pageId))){throw 'Restricted page remained accessible'}
  }
  foreach($id in @(0,3,5,6,7)){
   $pageId=[Enum]::ToObject($asm.GetType('PCL.FormMain+PageType'),$id)
   if(Call-Mode 'BlocksPage' @($pageId)){throw 'Required page was blocked'}
  }
  $setupUi=[Enum]::ToObject($asm.GetType('PCL.FormMain+PageSubType'),2)
  $page=$left.PageGet($setupUi)
  if($page.GetType().FullName -ne 'PCL.PageSetupModpack'){throw 'Full settings remained accessible'}
  $selector=[Activator]::CreateInstance($asm.GetType('PCL.PageSelectLeft'))
  $selector.GetType().GetMethod('RenderPackFolderList',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($selector,@()) | Out-Null
  $folderRows=@($selector.FindName('PanList').Children | Where-Object {$_.GetType().Name -eq 'MyListItem'})
  if($folderRows.Count -ne 1 -or $folderRows[0].ContextMenu -or $folderRows[0].Buttons){throw 'Pack folder selection exposed add/import or folder management'}
  $expectedRoot=$asm.GetType('PCL.MinecraftFolderPolicy').GetMethod('PackFolder',$flags).Invoke($null,@([MeloongCore.Paths]::Base))
  if($folderRows[0].Tag.Location -ne $expectedRoot){throw 'Pack folder selection escaped its own launcher directory'}
  foreach($relative in @('versions\Allowed\','versions\..\outside\','versions\Nested\Child\')){
   $allowed=Call-Mode 'AllowsInstance' @($expectedRoot+$relative)
   if($allowed -ne ($relative -eq 'versions\Allowed\')){throw 'Pack instance folder boundary failed'}
  }
  $sharedBefore=$entries['LaunchFolders'].ValueCache['']
  $asm.GetType('PCL.PageSelectLeft').GetMethod('AddFolder').Invoke($null,@('Z:\synthetic-pack\','synthetic',$false)) | Out-Null
  if($entries['LaunchFolders'].ValueCache[''] -cne $sharedBefore){throw 'First pack registration changed the shared directory catalog'}
 }
 $page.GetType().GetMethod('PageLoaded',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($page,@()) | Out-Null
 if(-not $page.FindName('CheckModpackMode').Checked -or -not $page.FindName('LabVersion').Text.StartsWith('LivingPCL ')){throw 'Compact page state/version failed'}
 if([string]::IsNullOrEmpty($page.FindName('CardLauncher').MainTextBlock.Text)){throw 'Launcher card title is missing'}
 if([Windows.Controls.Grid]::GetColumn($page.FindName('CheckModpackMode')) -ne 1){throw 'Mode checkbox does not align with the other setting values'}
 foreach($name in @('BtnSystemUpdate','ComboSystemActivity','BtnSystemSettingImp','BtnSystemSettingExp','CardDebug')){if($page.FindName($name)){throw 'Unexpected full-setting control in compact page'}}
 if(-not $page.FindName('TextSystemCache')){throw 'Cache folder setting missing'}
 $entry.ValueCache['']=$false
 [ModpackConfirmationChecks]::CheckModWarnings($mode,$false)
 if(-not (Call-Mode 'CanChangeMinecraft' @('26.2','26.1'))){throw 'Normal Minecraft editing was restricted'}
 if((Call-Mode 'DisplayTitle' @('LivingPCL')) -cne 'LivingPCL'){throw 'Normal title did not return'}
 $asm.GetType('PCL.PageSetupUI').GetField('_HiddenForceShow',$flags).SetValue($null,$false)
 Call-Mode 'ApplyVisibility' @(,$null) | Out-Null
 Call-Mode 'ApplyHeader' @($header,$buttons[1],$buttons[2],$buttons[3],$buttons[4]) | Out-Null
 if($buttons[1].Visibility -ne 'Visible' -or $buttons[4].Visibility -ne 'Visible'){throw 'Normal top navigation did not return'}
 if($left.FindName('ItemUI').Visibility -ne 'Visible' -or $launch.FindName('BtnVersion').Visibility -ne 'Visible'){throw 'Normal navigation did not return'}
 if($overview.FindName('BtnManageDelete').Visibility -ne 'Visible' -or $overview.FindName('BtnDisplayStar').Visibility -ne 'Visible' -or $fakeHeader.Info -ne 'synthetic header details'){throw 'Normal instance actions/details did not return'}
 foreach($name in @('PageInstanceSetup','PageSetupLaunch')){if($instanceViews[$name].FindName('ComboArgumentIndieV2').Visibility -ne 'Visible'){throw 'Normal isolation row did not return'}}
 $system=$left.PageGet([Enum]::ToObject($asm.GetType('PCL.FormMain+PageSubType'),3))
 if($system.GetType().FullName -ne 'PCL.PageSetupSystem' -or -not $system.FindName('CheckModpackMode')){throw 'Normal launcher mode switch missing'}
 $animations=$asm.GetType('PCL.ModAnimation').GetField('_AniControlEnabled',[Reflection.BindingFlags]'NonPublic,Static')
 $oldAnimations=$animations.GetValue($null)
 $animations.SetValue($null,($oldAnimations+1))
 try{$system.Reload()}finally{$animations.SetValue($null,$oldAnimations)}
 if($system.FindName('CheckModpackMode').Visibility -ne 'Visible'){throw 'Exported launcher lost its mode switch'}
 $marker.ValueCache['']=$false
 $system.GetType().GetMethod('RefreshModpackSwitch',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($system,@()) | Out-Null
 if($system.FindName('CheckModpackMode').Visibility -ne 'Collapsed' -or $system.FindName('LabModpackMode').Visibility -ne 'Collapsed'){throw 'Unmarked launcher exposed the mode switch'}
 $marker.ValueCache['']=$true
 $system.GetType().GetMethod('RefreshModpackSwitch',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($system,@()) | Out-Null
 if(-not $system.FindName('LabLauncherVersion').Text.StartsWith('LivingPCL ') -or [Windows.Controls.Grid]::GetRow($system.FindName('LabLauncherVersion')) -ne 2){throw 'Normal launcher version line must follow the update source'}
 foreach($key in $preferences.Keys){if($preferences[$key] -ne $entries[$key].ValueCache['']){throw 'Mode altered existing hidden preferences'}}
 $export=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceExport'))
 $packOption=$export.FindName('CheckOptionsModpackMode')
 if(-not $packOption.Checked -or -not $packOption.Tag.DefaultChecked){throw 'Export mode is not selected by default'}
 $export.FindName('CheckOptionsPcl').Checked=$false
 if($packOption.Parent.Visibility -ne 'Collapsed'){throw 'Pack option visible without a launcher'}
 $export.FindName('CheckOptionsPcl').Checked=$true
 if($packOption.Parent.Visibility -ne 'Visible'){throw 'Pack option hidden with a launcher'}
 [IO.Directory]::CreateDirectory($fixture) | Out-Null
 foreach($enabled in @($true,$false)){
  $destination=Join-Path $fixture ([string]$enabled)
  [IO.Directory]::CreateDirectory((Join-Path $destination 'PCL')) | Out-Null
  $config=Join-Path $destination 'PCL/Setup.ini'
  [IO.File]::WriteAllText($config,"Unrelated:preserve`r`nLivingModpackMode:$(-not $enabled)`r`n")
  Call-Mode 'ConfigureExport' @($destination,$true,$enabled) | Out-Null
  $text=[IO.File]::ReadAllText($config)
  if(-not $text.Contains('Unrelated:preserve') -or -not $text.Contains('LivingModpackExported:True') -or -not $text.Contains('LivingModpackMode:'+([string]$enabled))){throw 'Export marker/mode failed to override copied state or changed unrelated data'}
 }
 $withoutLauncher=Join-Path $fixture 'no-launcher'
 Call-Mode 'ConfigureExport' @($withoutLauncher,$false,$true) | Out-Null
 if([IO.Directory]::Exists($withoutLauncher)){throw 'Standalone modpack acquired launcher settings'}
 if($entry.ValueCache[''] -ne $false){throw 'Export changed the producing launcher mode'}
 $mods=Join-Path $fixture 'mods'
 [IO.Directory]::CreateDirectory($mods) | Out-Null
 [ModpackConfirmationChecks]::WriteMod((Join-Path $mods 'renamed.jar'),'fabric-api','0.155.2')
 [ModpackConfirmationChecks]::WriteMod((Join-Path $mods 'unrelated.jar'),'fixture-only','1.0')
 [IO.File]::WriteAllText((Join-Path $mods 'broken.jar'),'broken-archive')
 [IO.File]::WriteAllText((Join-Path $mods 'inactive.jar.disabled'),'inactive')
 $summary=$asm.GetType('PCL.InstalledComponentSummary',$true)
 $localMods=$summary.GetMethod('ReadLocalMods',$flags).Invoke($null,@([string]$mods))
 if($localMods.Count -ne 1 -or $localMods['fabric-api'] -ne '0.155.2'){throw 'Local installed-component metadata scan failed'}
 $version=[PCLCS.McVersion]::new('26.2',$true)
 $version.ModLoaderVersions[[PCLCS.ModLoaders]::Fabric]='0.19.3'
 $rows=$summary.GetMethod('Describe',$flags).Invoke($null,@($version,$null,$localMods))
 if(($rows.Name -join ',') -cne 'Minecraft,Fabric,Fabric API'){throw 'Overview included uninstalled or incompatible components'}
 $entry.ValueCache['']=$true
 Call-Mode 'ApplyInstanceVisibility' @() | Out-Null
 $overview.GetType().GetMethod('ShowInstalledComponents',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($overview,[object[]](,$rows)) | Out-Null
 $cards=$overview.FindName('PanInstalledComponents').Children
 if($cards.Count -ne 3 -or @($cards | Where-Object {$_.IsHitTestVisible}).Count){throw 'Installed overview components must be display-only cards'}
 Write-Output 'Instance mode checks passed: delete/favorite/export/download hidden, isolation editable, modify preserved, Minecraft locked, mod warning cancellation, local component metadata and read-only installed cards.'
 if($PreviewDirectory){
  $preview=[IO.Path]::GetFullPath($PreviewDirectory)
  [IO.Directory]::CreateDirectory($preview) | Out-Null
  foreach($width in @(700,520)){
   $hostPanel=[Windows.Controls.Border]::new()
   $hostPanel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#E7EFFB')
   $hostPanel.Width=$width;$hostPanel.Height=360;$hostPanel.Child=$page
   $hostPanel.Measure([Windows.Size]::new($width,360));$hostPanel.Arrange([Windows.Rect]::new(0,0,$width,360));$hostPanel.UpdateLayout()
   $image=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,360,96,96,[Windows.Media.PixelFormats]::Pbgra32)
   $image.Render($hostPanel)
   $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
   $stream=[IO.File]::Create((Join-Path $preview ('modpack-settings-'+$width+'.png')))
   try{$encoder.Save($stream)}finally{$stream.Dispose()}
   $hostPanel.Child=$null
  }
  $normalCard=$system.FindName('LabLauncherVersion').Parent
  while($normalCard -and $normalCard.GetType().FullName -ne 'PCL.MyCard'){$normalCard=$normalCard.Parent}
  if(-not $normalCard){throw 'Normal launcher card not found for preview'}
  $system.FindName('PanDonate').Visibility='Collapsed'
  $normalCard.UseAnimation=$false
  $null=$normalCard.MainTextBlock
  $normalCard.Parent.Children.Remove($normalCard)
  $normalPreview=[Windows.Controls.StackPanel]::new()
  $normalPreview.Margin=[Windows.Thickness]::new(25)
  $normalPreview.Children.Add($normalCard) | Out-Null
  $hostPanel=[Windows.Controls.Border]::new()
  $hostPanel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#E7EFFB')
  $hostPanel.Width=700;$hostPanel.Height=400;$hostPanel.Child=$normalPreview
  $hostPanel.Measure([Windows.Size]::new(700,400));$hostPanel.Arrange([Windows.Rect]::new(0,0,700,400));$hostPanel.UpdateLayout()
  $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(700,400,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $image.Render($hostPanel)
  $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
  $stream=[IO.File]::Create((Join-Path $preview 'normal-launcher-settings.png'))
  try{$encoder.Save($stream)}finally{$stream.Dispose()}
  $hostPanel.Child=$null
  $headerCard=$overview.FindName('PanDisplayItem').Parent
  $overviewPanel=$overview.FindName('PanMain')
  $componentPanel=$overview.FindName('PanInstalledComponents')
  $overviewPanel.Children.Remove($headerCard)
  $overviewPanel.Children.Remove($componentPanel)
  $componentPreview=[Windows.Controls.StackPanel]::new()
  $componentPreview.Margin=[Windows.Thickness]::new(25,10,25,10)
  $componentPreview.Children.Add($headerCard) | Out-Null
  $componentPreview.Children.Add($componentPanel) | Out-Null
  $hostPanel=[Windows.Controls.Border]::new()
  $hostPanel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#E7EFFB')
  $hostPanel.Width=700;$hostPanel.Height=350;$hostPanel.Child=$componentPreview
  $hostPanel.Measure([Windows.Size]::new(700,350));$hostPanel.Arrange([Windows.Rect]::new(0,0,700,350));$hostPanel.UpdateLayout()
  $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(700,350,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $image.Render($hostPanel)
  $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
  $stream=[IO.File]::Create((Join-Path $preview 'installed-components.png'))
  try{$encoder.Save($stream)}finally{$stream.Dispose()}
  $hostPanel.Child=$null
 }
 Write-Output 'Modpack mode checks passed: export marker required, unmarked switch hidden, warning cancellation, own-folder selection, shared catalog protected, restricted pages, F12 handling, compact/full settings and isolated export configuration.'
}finally{
 $entry.ValueCache['']=$false
 $full=[IO.Path]::GetFullPath($fixture)
 $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-ModpackMode-[a-f0-9]{32}\z'){throw 'Unsafe fixture cleanup'}
 if([IO.Directory]::Exists($full)){
  foreach($path in [IO.Directory]::EnumerateFileSystemEntries($full,'*',[IO.SearchOption]::AllDirectories)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected fixture link'}}
  [IO.Directory]::Delete($full,$true)
 }
 $app.Shutdown()
}
