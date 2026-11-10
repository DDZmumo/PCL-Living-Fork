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
function Assert-StoreException([string]$name,[object[]]$values,[type]$expected,[string]$expectedMessage){
 $observed=$null
 try{Invoke-Store $name $values|Out-Null}catch{
  $observed=$_.Exception
  while(($observed -is [Reflection.TargetInvocationException] -or $observed -is [Management.Automation.MethodInvocationException]) -and $null -ne $observed.InnerException){$observed=$observed.InnerException}
 }
 if($null -eq $observed -or $observed.GetType() -ne $expected){$actual=if($null -eq $observed){'no exception'}else{$observed.GetType().FullName};throw ('Unexpected resource mapping exception for '+$name+'; expected '+$expected.FullName+'; observed '+$actual)}
 if(-not [string]::IsNullOrEmpty($expectedMessage) -and $observed.Message -cne $expectedMessage){throw ('Resource validation message changed for '+$name)}
}
function Read-PackDetail($entry,$detail){
 [object[]]$values=@($entry,$detail)
 $store.GetMethod('ReadPackInfo',$flags).Invoke($null,$values)|Out-Null
 return ,$values[1]
}
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
 # Old BAML names the non-inheriting attached-property owner. It must resolve the
 # same native DP and original callback, without touching the system clipboard.
 $clipboardOwner=$asm.GetType('PCL.Controls.Behaviors.ClipboardInterceptor',$true)
 $clipboardProperty=$clipboardOwner.GetField('EnableSafeClipboardProperty',$flags).GetValue($null)
 $nativeClipboard=[PCLLF.Presentation.Controls.ClipboardInterceptorView]::EnableSafeClipboardProperty
 if(-not [object]::ReferenceEquals($clipboardProperty,$nativeClipboard)){throw 'Legacy clipboard owner created a duplicate DP'}
 foreach($item in @(@([Windows.Controls.TextBox]::new(),3),@([Windows.Controls.RichTextBox]::new(),3),@([Windows.Controls.DataGrid]::new(),1))){
  $control=$item[0];$added=[int]$item[1];$before=$control.CommandBindings.Count
  if([bool]$control.GetValue($clipboardProperty)){throw 'Clipboard attached property default changed'}
  $control.SetValue($clipboardProperty,$true)
  if($control.CommandBindings.Count -ne $before+$added){throw 'Actual clipboard callback did not add original command bindings'}
  $control.SetValue($clipboardProperty,$true)
  if($control.CommandBindings.Count -ne $before+$added){throw 'Same clipboard property value repeated the change callback'}
  $control.SetValue($clipboardProperty,$false);$control.SetValue($clipboardProperty,$true)
  if($control.CommandBindings.Count -ne $before+2*$added){throw 'Clipboard false-to-true transition lost original binding accumulation'}
  $control.CommandBindings.Clear()
 }
 Write-Output 'PASS: old BAML clipboard owner shares the native DP and original false/true callback bindings; no clipboard command executed.'
 # Preserve original name-only reflection across the native base-class boundary.
 $legacyPageType=$asm.GetType('PCL.MyPageRight',$true)
 $legacyStateType=$asm.GetType('PCL.MyPageRight+PageStates',$true)
 foreach($typeName in @('PCL.MyPageRight','PCL.PageInstanceMod')){
  $pageType=$asm.GetType($typeName,$true)
  $property=$pageType.GetProperty('PageState');$getter=$pageType.GetMethod('get_PageState');$setter=$pageType.GetMethod('set_PageState')
  if($null -eq $property -or $property.PropertyType -ne $legacyStateType -or $getter.ReturnType -ne $legacyStateType -or $setter.GetParameters()[0].ParameterType -ne $legacyStateType){throw 'Original page state name-only lookup lost its unique old enum contract'}
 }
 $page=[Activator]::CreateInstance($legacyPageType)
 $originalState=$legacyPageType.GetProperty('PageState');$nativeState=[PCLLF.Presentation.Controls.MyPageRightView].GetProperty('NativePageState')
 $savedState=$originalState.GetValue($page,$null)
 try{
  $originalState.SetValue($page,[Enum]::Parse($legacyStateType,'ContentStay'),$null)
  if($nativeState.GetValue($page,$null).ToString() -cne 'ContentStay'){throw 'Legacy page state write did not reach the actual native store'}
  $nativeState.SetValue($page,[Enum]::Parse($nativeState.PropertyType,'ContentExit'),$null)
  if($originalState.GetValue($page,$null).ToString() -cne 'ContentExit'){throw 'Native page state write did not reach the original enum property'}
 }finally{$originalState.SetValue($page,$savedState,$null)}
 Write-Output 'PASS: original page property/getter/setter name-only reflection and bidirectional live native state remain unique.'
 $kindNames=@('Saves','Screenshots','ResourcePacks','ShaderPacks','Schematics')
 $kindTitles=@('存档','截图','资源包','光影包','投影/原理图')
 $kindFolders=@('saves','screenshots','resourcepacks','shaderpacks','schematics')
 if($kindType.FullName -cne 'PCL.InstanceResourceKind' -or [Enum]::GetUnderlyingType($kindType) -ne [int] -or ([Enum]::GetNames($kindType) -join '|') -cne ($kindNames -join '|')){throw 'Resource kind lost its original VB enum identity or five names'}
 for($index=0;$index -lt $kindNames.Length;$index++){
  $kind=[Enum]::ToObject($kindType,[int]$index)
  if($kind.ToString() -cne $kindNames[$index] -or (Invoke-Store 'Title' @($kind)) -cne $kindTitles[$index] -or (Invoke-Store 'FolderName' @($kind)) -cne $kindFolders[$index]){throw 'Resource kind numeric values, title or folder mapping changed'}
  $gameFolder='relative\mixed/slash'
  if((Invoke-Store 'Root' @($gameFolder,$kind)) -cne [IO.Path]::Combine($gameFolder,$kindFolders[$index])){throw 'Resource Root changed its original Path.Combine behavior'}
 }
 foreach($invalid in @(-1,5,[int]::MinValue,[int]::MaxValue)){
  $kind=[Enum]::ToObject($kindType,[int]$invalid)
  foreach($method in @('Title','FolderName')){Assert-StoreException $method @($kind) ([IndexOutOfRangeException])}
  Assert-StoreException 'Root' @($null,$kind) ([IndexOutOfRangeException])
 }
 Assert-StoreException 'Root' @($null,([Enum]::ToObject($kindType,0))) ([ArgumentNullException])
 $invalidName='文件名无效，请使用不含路径和特殊字符的名称。'
 $systemName='此名称为系统保留名称。'
 $launcherName='此名称为启动器临时目录保留。'
 $supplementary=[char]::ConvertFromUtf32(0x1f4be)
 foreach($name in @('World name','截图 2026-11-03','CONSOLE','COM0','COM10','LPT0','LPT10','XCON','.pcl','.pclx-test','.hidden','pack..name',('a'*120),($supplementary*60))){if((Invoke-Store 'SafeName' @($name)) -cne $name){throw 'Resource name validation changed an accepted name'}}
 Assert-StoreException 'SafeName' @($null) ([IO.IOException]) $invalidName
 foreach($name in @('',' ',([string][char]9),([string][char]0x00a0),' name','name ','file.','.','..',('a'*121),(($supplementary*60)+'a'),'CON.',' CON','.pcl-bad/')){Assert-StoreException 'SafeName' @($name) ([IO.IOException]) $invalidName}
 # Fixed original Windows filename set, independent of the implementation's current Path table.
 $forbidden=@([char[]](0..31))+@([char[]]'<>:"/\|?*')
 foreach($character in $forbidden){Assert-StoreException 'SafeName' @(('a'+[string]$character+'b')) ([IO.IOException]) $invalidName}
 $reserved=@('CON','PRN','AUX','NUL')+@(1..9|ForEach-Object {'COM'+$_;'LPT'+$_})
 foreach($name in $reserved){Assert-StoreException 'SafeName' @($name) ([IO.IOException]) $systemName;Assert-StoreException 'SafeName' @($name.ToLowerInvariant()+'.txt') ([IO.IOException]) $systemName}
 foreach($name in @('.pcl-','.PCL-test')){Assert-StoreException 'SafeName' @($name) ([IO.IOException]) $launcherName}
 $sizeSamples=@(
  @{Value=[long]-1;Text='-1 B'},@{Value=[long]0;Text='0 B'},@{Value=[long]1023;Text='1023 B'},
  @{Value=[long]1024;Text='1 KB'},@{Value=[long]1536;Text='1.5 KB'},@{Value=[long]1048576;Text='1 MB'},
  @{Value=[long]1073741824;Text='1 GB'},@{Value=[long]1099511627776;Text='1024 GB'},
  @{Value=[long]::MinValue;Text='-9223372036854775808 B'},@{Value=[long]::MaxValue;Text='8589934592 GB'}
 )
 $sizeCulture=[Threading.Thread]::CurrentThread.CurrentCulture
 try{
  foreach($culture in @('en-US','fr-FR')){
   [Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo($culture)
   foreach($sample in $sizeSamples){$expected=$sample.Text;if($culture -eq 'fr-FR'){$expected=$expected.Replace('.',',')};if((Invoke-Store 'FormatSize' @($sample.Value)) -cne $expected){throw 'Resource size units, boundary or current-culture formatting changed'}}
  }
 }finally{[Threading.Thread]::CurrentThread.CurrentCulture=$sizeCulture}
 $fileExtensions=@('.png','.jpg','.jpeg','.bmp','.zip','.litematic','.schem','.schematic','.nbt')
 $acceptedExtensions=@{0=@();1=@('.png','.jpg','.jpeg','.bmp');2=@('.zip');3=@('.zip');4=@('.litematic','.schem','.schematic','.nbt')}
 for($index=0;$index -lt 5;$index++){
  $kind=[Enum]::ToObject($kindType,[int]$index)
  foreach($extension in $fileExtensions){
   $expected=$acceptedExtensions[$index] -contains $extension
   foreach($name in @(('resource'+$extension),('relative.folder/FILE'+$extension.ToUpperInvariant()),$extension)){if((Invoke-Store 'AcceptFile' @($kind,$name)) -ne $expected){throw 'Resource extension whitelist changed'}}
  }
  foreach($name in @('resource','resource.txt','resource.jar','file.png:stream','file.zip\','file.nbt.','file.jpg ')){if(Invoke-Store 'AcceptFile' @($kind,$name)){throw 'Resource extension filter accepted a suffix or unlisted type'}}
  Assert-StoreException 'AcceptFile' @($kind,$null) ([NullReferenceException])
 }
 foreach($invalid in @(-1,5,[int]::MinValue,[int]::MaxValue)){
  $kind=[Enum]::ToObject($kindType,[int]$invalid)
  if(Invoke-Store 'AcceptFile' @($kind,'valid.png')){throw 'Resource extension filter accepted an unknown kind'}
  Assert-StoreException 'AcceptFile' @($kind,$null) ([NullReferenceException])
 }
 $pathForbidden=@([char[]](0..31))+@([char[]]'"<>|')
 foreach($character in $pathForbidden){foreach($index in @(0,1,-1)){$kind=[Enum]::ToObject($kindType,[int]$index);Assert-StoreException 'AcceptFile' @($kind,('bad'+[string]$character+'name.png')) ([ArgumentException])}}
 $imageKindForExtension=[Enum]::ToObject($kindType,1)
 foreach($name in @('bad?name.png','bad*name.PNG','drive:name.jpg','file.png:stream.jpg')){if(-not (Invoke-Store 'AcceptFile' @($imageKindForExtension,$name))){throw 'Resource extension filter incorrectly applied filename or ADS rejection'}}
 foreach($device in @('\\?\C:\bad|name.png','\??\C:\bad|name.png','\\.\C:\bad|name.png')){
  # PowerShell's CLR4 domain can have a different legacy-path switch from LF.
  # Compare the bridge to its real host API here; the modern cases pin LF defaults.
  $hostError=$null
  try{$extension=[IO.Path]::GetExtension($device);if($extension -cne '.png'){throw 'Unexpected host device extension'}}catch{$hostError=$_.Exception;while($null -ne $hostError.InnerException){$hostError=$hostError.InnerException}}
  if($null -ne $hostError){if($hostError.GetType() -ne [ArgumentException]){throw $hostError};Assert-StoreException 'AcceptFile' @($imageKindForExtension,$device) ([ArgumentException])}
  elseif(-not (Invoke-Store 'AcceptFile' @($imageKindForExtension,$device))){throw 'Resource extension filter changed the host device exception policy'}
  Assert-StoreException 'AcceptFile' @($imageKindForExtension,('  '+$device)) ([ArgumentException])
 }
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
 $resourceType=$worlds[0].GetType()
 if($resourceType.FullName -cne 'PCL.InstanceResourceEntry' -or -not $resourceType.IsSealed -or $resourceType.BaseType.FullName -cne 'PCLCS.InstanceResourceEntryData'){throw 'Resource rows lost their original sealed VB type or production DTO base'}
 $resourceFields=@{Name=[string];FullPath=[string];Description=[string];IsDirectory=[bool];ModifiedUtc=[DateTime];CreatedUtc=[DateTime];Length=[long]}
 foreach($name in $resourceFields.Keys){$field=$resourceType.GetField($name,$flags);if($null -eq $field -or $field.FieldType -ne $resourceFields[$name] -or $field.DeclaringType -ne $resourceType.BaseType){throw ('Inherited resource field contract changed: '+$name)}}
 $iconField=$resourceType.GetField('Icon',$flags)
 if($null -eq $iconField -or $iconField.FieldType -ne [Windows.Media.ImageSource] -or $iconField.DeclaringType -ne $resourceType -or $null -eq $worlds[0].Icon -or -not $worlds[0].Icon.IsFrozen){throw 'Resource row did not retain its original WPF Icon field and loaded image'}
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
 $metadataFolder=Join-Path $fixture 'metadata-detail'
 $metadataFile=Join-Path $metadataFolder 'pack.mcmeta'
 $metadataEntry=[Activator]::CreateInstance($resourceType,$true)
 $metadataEntry.FullPath=$metadataFolder;$metadataEntry.IsDirectory=$true
 foreach($sample in @(
  @{Json='{"pack":{"description":"first\r\nsecond\u2028third","pack_format":46}}';Text='first second third · 资源格式 46'},
  @{Json='{"pack":{"description":{"text":"\nX\u0085Y"},"pack_format":null}}';Text=' X Y · 资源格式 '},
  @{Json='{"pack":{"description":{"text":1234}}}';Text='1234'},
  @{Json='{}';Text='seed'},@{Json='{"pack":[]}';Text='seed'},
  @{Json='{"pack":{"description":[]}}';Text='seed'},@{Json='not JSON';Text='seed'}
 )){
  Put $metadataFile $sample.Json
  if(-not [string]::Equals((Read-PackDetail $metadataEntry 'seed'),$sample.Text,[StringComparison]::Ordinal)){throw 'Resource pack detail parsing, newline or swallowed-error behavior changed'}
 }
 $longDescription=('a'*179)+$supplementary
 Put $metadataFile ('{"pack":{"description":"'+$longDescription+'"}}')
 $actualDetail=Read-PackDetail $metadataEntry 'seed'
 if(-not [string]::Equals($actualDetail,$longDescription.Substring(0,180),[StringComparison]::Ordinal) -or -not [char]::IsHighSurrogate($actualDetail[179])){throw 'Resource pack detail did not retain the original 180 UTF16-unit boundary'}
 foreach($json in @('{"pack":null}','{"pack":{"description":null}}')){Put $metadataFile $json;Assert-StoreException 'ReadPackInfo' @($metadataEntry,'seed') ([InvalidOperationException])}
 $missingPackMessage='资源包缺少 pack.mcmeta，未替换原文件。'
 $missingShaderMessage='光影包缺少 shaders 目录，未替换原文件。'
 $unknownKind=[Enum]::ToObject($kindType,5)
 $emptyPackage=Join-Path $fixture 'empty-package.zip';New-Zip $emptyPackage @{}
 $wrongCasePackage=Join-Path $fixture 'wrong-case-package.zip';New-Zip $wrongCasePackage @{'Pack.mcmeta'='not JSON'}
 $unparsedPackage=Join-Path $fixture 'unparsed-package.zip';New-Zip $unparsedPackage @{'pack.mcmeta'='not JSON'}
 $shaderPackage=Join-Path $fixture 'shader-package.zip';New-Zip $shaderPackage @{'SHADERS/only.vert'='not inspected'}
 $shaderDirectory=Join-Path $fixture 'shader-directory.zip';New-Zip $shaderDirectory @{'shaders/'=''}
 $wrongShader=Join-Path $fixture 'wrong-shader-package.zip';New-Zip $wrongShader @{'nested/shaders/file.vert'='not root';'shaders\file.vert'='wrong separator'}
 foreach($path in @($pack,$unparsedPackage)){Invoke-Store 'ValidatePackage' @($path,$packKind)|Out-Null}
 foreach($path in @($shaderPackage,$shaderDirectory)){Invoke-Store 'ValidatePackage' @($path,$shaderKind)|Out-Null}
 foreach($path in @($emptyPackage,$wrongCasePackage)){Assert-StoreException 'ValidatePackage' @($path,$packKind) ([IO.IOException]) $missingPackMessage}
 foreach($path in @($emptyPackage,$wrongShader)){Assert-StoreException 'ValidatePackage' @($path,$shaderKind) ([IO.IOException]) $missingShaderMessage}
 Invoke-Store 'ValidatePackage' @($emptyPackage,$unknownKind)|Out-Null
 Assert-StoreException 'ValidatePackage' @((Join-Path $fixture 'missing-package.zip'),$unknownKind) ([IO.FileNotFoundException])
 $badPackage=Join-Path $fixture 'bad-package.zip';[IO.File]::WriteAllText($badPackage,'not ZIP')
 Assert-StoreException 'ValidatePackage' @($badPackage,$unknownKind) ([IO.InvalidDataException])
 # A valid EOCD with a corrupt central directory verifies that unknown kinds do
 # not start reading entries merely because archive opening has succeeded.
 $lazyPackage=Join-Path $fixture 'lazy-central-package.zip';New-Zip $lazyPackage @{'file.txt'='owned'}
 $lazyBytes=[IO.File]::ReadAllBytes($lazyPackage);$central=-1
 for($i=0;$i -lt $lazyBytes.Length-3;$i++){if($lazyBytes[$i] -eq 0x50 -and $lazyBytes[$i+1] -eq 0x4b -and $lazyBytes[$i+2] -eq 0x01 -and $lazyBytes[$i+3] -eq 0x02){if($central -ne -1){throw 'Ambiguous synthetic central directory'};$central=$i}}
 if($central -lt 0){throw 'Synthetic central directory was not found'}
 $lazyBytes[$central]=0;[IO.File]::WriteAllBytes($lazyPackage,$lazyBytes)
 Invoke-Store 'ValidatePackage' @($lazyPackage,$unknownKind)|Out-Null
 Assert-StoreException 'ValidatePackage' @($lazyPackage,$packKind) ([IO.InvalidDataException])
 Assert-StoreException 'ValidatePackage' @($lazyPackage,$shaderKind) ([IO.InvalidDataException])
 foreach($path in @($pack,$emptyPackage,$wrongCasePackage,$unparsedPackage,$shaderPackage,$shaderDirectory,$wrongShader,$badPackage,$lazyPackage)){
  $exclusive=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None);$exclusive.Dispose()
 }
 Invoke-Store 'ImportFile' @($packs,$packs,$packKind,$pack,$none)|Out-Null
 $packEntries=Scan $packs $packKind
 if($packEntries.Count -ne 1 -or -not $packEntries[0].Description.Contains('Synthetic native pack')){throw 'Resource-pack metadata was not read'}
 Assert-Rejected {Invoke-Store 'ImportFile' @($packs,$packs,$packKind,$pack,$none)} 'Resource import overwrote an existing file'
 $renamed=Invoke-Store 'Rename' @($packs,$packEntries[0],'Renamed.zip')
 if(-not [IO.File]::Exists($renamed)){throw 'Resource rename failed or retained an archive handle'}
 $trash=Join-Path $mc 'PCL/ResourceRecycle/ResourcePacks'
 $deleteEntries=Scan $packs $packKind
 Invoke-Store 'DeleteMany' @($packs,$deleteEntries.PSObject.BaseObject,$none)|Out-Null
 if([IO.File]::Exists($renamed) -or [IO.Directory]::Exists($trash)){throw 'Direct deletion retained resource payload'}
 Invoke-Store 'ImportFile' @($packs,$packs,$packKind,$pack,$none)|Out-Null
 $stale=Scan $packs $packKind;[IO.File]::AppendAllText($stale[0].FullPath,'changed-after-listing')
 Assert-Rejected {Invoke-Store 'DeleteMany' @($packs,$stale.PSObject.BaseObject,$none)} 'A changed resource was deleted through a stale selection'
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
 if(@($packPage.FindName('BtnOpenFolder').Parent.Children|Where-Object {$_.Visibility -ne 'Collapsed'}).Count -ne 4 -or $packPage.FindName('BtnRestore')){throw 'Resource toolbar lost its native actions or top recycle-bin entry'}
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
 Invoke-Store 'DeleteMany' @($packs,$recycleEntries.PSObject.BaseObject,$none)|Out-Null
 if((Scan $packs $packKind).Count -ne 0){throw 'Batch deletion left selected resources live'}
 $items=Scan $packs $packKind
 $pageType.GetMethod('ShowResources',$flags).Invoke($packPage,@([string]$packs,[string]$packs,$items.PSObject.BaseObject))|Out-Null
 $emptyPage=$left.PageGet([Enum]::ToObject($subType,7))
 $emptyItems=[Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($asm.GetType('PCL.InstanceResourceEntry')))
 $pageType.GetMethod('ShowResources',$flags).Invoke($emptyPage,@([string]$shots,[string]$shots,$emptyItems.PSObject.BaseObject))|Out-Null
 if($emptyPage.FindName('PanEmpty').Visibility -ne 'Collapsed' -or $emptyPage.FindName('PanBack').Visibility -ne 'Visible' -or $emptyPage.FindName('BtnRestore')){throw 'Empty resources lost the toolbar and recycle-bin entry'}
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
 Write-Output 'Instance resources passed: original enum/title/folder/error, safe-name/UTF16/message, size/current-culture, pack-description/ref/UTF16/error, extension-filter/null/device and package/lazy-directory/handle contracts, five native pages, compact checkbox rows/hover actions, native filters/action bar, single/bulk selection, hidden-selection clearing, online update filter, bulk export/recycle/restore, world round trips, metadata/preview handle release, import collision/cancellation and path/stale-entry protection.'
}catch{
 throw ($_.Exception.ToString())
}finally{
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-Resources-[a-f0-9]{32}\z'){throw 'Unsafe resource fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
 $app.Shutdown()
}
