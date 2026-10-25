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
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclScreenshotGallery-'+[Guid]::NewGuid().ToString('N'))
try {
 $kind=Kind 'Screenshots';$shots=Join-Path $fixture 'screenshots'
 foreach($name in @('one.png','two.png','three.png')){Write-Png (Join-Path $shots $name)}
 $items=Scan $shots $kind
 $asm.GetType('PCL.ModSecret').GetMethod('ThemeRefresh').Invoke($null,@(0))|Out-Null
 $type=$asm.GetType('PCL.PageInstanceResources');$page=$type.GetConstructor($flags,$null,[type[]]@($kindType),$null).Invoke(@($kind))
 $type.GetMethod('ShowResources',$flags).Invoke($page,[object[]]@([string]$shots,[string]$shots,$items.PSObject.BaseObject))|Out-Null
 $grid=$page.FindName('PanScreenshots')
 if($grid.Columns -ne 2 -or $grid.Children.Count -ne 3 -or $grid.Visibility -ne 'Visible' -or $page.FindName('CardPreview')){throw 'Screenshot page is not a two-column gallery without an internal preview'}
 $script:opened=0;$script:info=$null
 $open=[Action[Diagnostics.ProcessStartInfo]]{param($info) $script:opened++;$script:info=$info}
 $click=$type.GetMethod('ScreenshotClick',$flags)
 $click.Invoke($page,[object[]]@($items[0],1,$open))|Out-Null
 if($script:opened -ne 0 -or -not $grid.Children[0].Checked){throw 'Single click opened a viewer or did not select'}
 $click.Invoke($page,[object[]]@($items[0],2,$open))|Out-Null
 if($script:opened -ne 1 -or -not $script:info.UseShellExecute -or $script:info.FileName -cne $items[0].FullPath -or $script:info.Arguments -ne ''){throw 'Double click did not dispatch the image to the system file association'}
 $click.Invoke($page,[object[]]@($items[1],1,$open))|Out-Null
 if($grid.Children[0].Checked -or -not $grid.Children[1].Checked -or $script:opened -ne 1){throw 'Second single-click selection failed'}
 $type.GetMethod('ChangeAllSelected',$flags).Invoke($page,@($true))|Out-Null
 if(@($grid.Children|Where-Object Checked).Count -ne 3 -or $page.FindName('CardSelect').Visibility -ne 'Visible' -or -not $page.FindName('BtnExport').IsEnabled){throw 'Gallery multiselect or bulk footer unavailable'}
 $type.GetMethod('ChangeAllSelected',$flags).Invoke($page,@($false))|Out-Null
 if($page.FindName('CardSelect').Visibility -ne 'Collapsed'){throw 'Gallery deselection did not collapse the footer'}
 $page.FindName('SearchBox').Text='two'
 if($grid.Children.Count -ne 1){throw 'Screenshot gallery search failed'}
 $page.FindName('SearchBox').Text=''
 $page.FindName('PanLoad').Visibility='Collapsed';$page.FindName('PanAllBack').Visibility='Visible';$page.FindName('PanAllBack').Opacity=1
 foreach($tile in $grid.Children){$entry=$tile.GetType().GetField('Entry',$flags).GetValue($tile);$tile.FindName('Thumbnail').Source=Invoke-Store 'LoadImage' @($entry.FullPath,640)}
 $panel=[Windows.Controls.Grid]::new();$panel.Background=[Windows.Media.BrushConverter]::new().ConvertFromString('#e4effa');$panel.Children.Add($page)|Out-Null
 if($PreviewDirectory){[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PreviewDirectory))|Out-Null;Render-Png $panel (Join-Path $PreviewDirectory 'screenshot-gallery.png') 780 720}
 $type.GetMethod('ScreenshotClick',$flags).Invoke($page,[object[]]@($items[0],1,$open))|Out-Null
 if($script:opened -ne 1){throw 'Selection unexpectedly launched another viewer'}
 Write-Output 'Screenshot gallery passed: two-column native cards, no in-app preview, single selection, double-click shell association, bulk footer, deselection, search and frozen thumbnails. No external viewer was started.'
}finally {
 $app.Shutdown()
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclScreenshotGallery-')){Remove-Item -LiteralPath $full -Recurse -Force}
}