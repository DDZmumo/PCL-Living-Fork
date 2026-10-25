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
try {
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0))|Out-Null
 $page=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceKeybindings'));$bindingType=$asm.GetType('PCL.InstanceKeybinding')
 $items=[Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($bindingType))
 foreach($sample in @(@('保存快捷栏','c',3),@('复制位置','c',3),@('调试崩溃','c',3),@('投影菜单','v',2),@('地图菜单','v',2),@('打开物品栏','e',1))){
  $item=[Activator]::CreateInstance($bindingType)
  foreach($pair in @(@('Key',('key_fixture.'+$items.Count)),@('Name',$sample[0]),@('Owners','Minecraft'),@('Value',('key.keyboard.'+$sample[1])),@('Normalized',('key.keyboard.'+$sample[1])),@('SharedCount',$sample[2]))){$bindingType.GetField($pair[0],$flags).SetValue($item,$pair[1])}
  $items.Add($item)
 }
 $page.GetType().GetField('Entries',$flags).SetValue($page,$items.PSObject.BaseObject)
 $render=$page.GetType().GetMethod('RenderList',$flags);$render.Invoke($page,@())|Out-Null
 $all=$page.FindName('PanBindings')
 if($all.Children.Count -ne 6 -or @($all.Children|Where-Object {$_.GetType().Name -eq 'MyCard'}).Count){throw 'All bindings should remain a flat list in default order'}
 for($i=0;$i -lt $items.Count;$i++){if($all.Children[$i].Children[0].Children[0].Text -cne $bindingType.GetField('Name',$flags).GetValue($items[$i])){throw 'All-bindings order was changed'}}
 $page.FindName('BtnFilterConflicts').Checked=$true
 $list=$page.FindName('PanBindings');$groups=@($list.Children|Where-Object {$_.GetType().Name -eq 'MyCard'})
 if($groups.Count -ne 2 -or $list.Children.Count -ne 2){throw 'Shared keys were not grouped independently from single keys'}
 $c=$groups|Where-Object {$_.Title -like 'C*'}|Select-Object -First 1
 if($c.Title -notlike '*与 2 项共用*'){throw 'Shared-key group count is wrong'}
 $rows=$c.Children|Where-Object {$_ -is [Windows.Controls.StackPanel]}|Select-Object -First 1
 if($rows.Children.Count -ne 3 -or $page.GetType().GetField('KeyButtons',$flags).GetValue($page).Count -ne 5){throw 'Grouping lost binding rows or edit buttons'}
 $page.FindName('SearchBox').Text='复制位置'
 $groups=@($list.Children|Where-Object {$_.GetType().Name -eq 'MyCard'})
 if($groups.Count -ne 1 -or $groups[0].Title -notlike '*与 2 项共用*'){throw 'Search lost shared-key context'}
 $page.FindName('SearchBox').Text='';$page.FindName('BtnFilterConflicts').Checked=$true
 if($list.Children.Count -ne 2){throw 'Shared-only filter left unshared rows'}
 $page.FindName('PanLoad').Visibility='Collapsed';$page.FindName('PanBack').Visibility='Visible';$page.FindName('PanBack').Opacity=1
 $panel=[Windows.Controls.Grid]::new();$panel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#e4effa');$panel.Children.Add($page)|Out-Null
 if($PreviewDirectory){[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory))|Out-Null;Render-Png $panel (Join-Path $PreviewDirectory 'keybinding-groups.png') 780 720}
 Write-Output 'Shared-key UI passed: one native card per key, correct title counts, complete editable rows, search and shared-only filter.'
}finally{$app.Shutdown()}
