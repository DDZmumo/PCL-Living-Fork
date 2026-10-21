param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# GUID temporary pack files and synthetic setting caches; no network, game launch or real settings.
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){
 [Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null
}
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$app=[Activator]::CreateInstance($asm.GetType('PCL.Application'),$true)
$app.InitializeComponent()
[Windows.Application]::ResourceAssembly=$asm
$entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
$entries['LaunchArgumentIndieV2'].ValueCache['']=3
$flags=[Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
$helper=$asm.GetType('PCL.ExportedVersionIsolation',$true)
$base=$asm.GetType('PCL.ModBase',$true)
function Invoke-Policy($name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return $helper.GetMethod($name,$flags).Invoke($null,$values)
}
function Read-Setting([string]$path,[string]$name){return $base.GetMethod('ReadIni',$flags).Invoke($null,@($path,$name,''))}
function Put-File([string]$path,[string]$text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null;[IO.File]::WriteAllText($path,$text)}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-ExportIsolation-'+[Guid]::NewGuid().ToString('N'))
try{
 $view=[Activator]::CreateInstance($asm.GetType('PCL.PageInstanceExport'))
 $option=$view.FindName('CheckAdvancedVersionIsolation')
 if(-not $option -or -not $option.Checked){throw 'The advanced isolation option must default to checked'}
 $original=Join-Path $fixture 'original/PCL/Setup.ini'
 Put-File $original "VersionArgumentIndieV2:False`r`nUnrelated:preserve-source`r`n"
 $originalBytes=[IO.File]::ReadAllBytes($original)
 foreach($enabled in @($true,$false)){
  $pack=Join-Path $fixture ('pack-'+$enabled)
  $overrides=Join-Path $pack 'overrides'
  $settings=Join-Path $overrides 'PCL/Setup.ini'
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settings))|Out-Null
  [IO.File]::Copy($original,$settings)
  Put-File (Join-Path $overrides 'mods/example.jar') 'synthetic-mod'
  Put-File (Join-Path $overrides 'saves/world/level.dat') 'synthetic-save'
  Put-File (Join-Path $overrides 'PCL/Logo.png') 'synthetic-logo'
  Put-File (Join-Path $overrides 'runtime/jre/bin/java.exe') 'synthetic-java-never-executed'
  Invoke-Policy 'ConfigureExport' @($overrides,$enabled,[string[]]@('runtime/jre'))|Out-Null
  if((Read-Setting $settings 'VersionArgumentIndieV2') -cne [string]$enabled -or (Read-Setting $settings 'VersionArgumentIndie') -ne [string]$(if($enabled){1}else{2})){throw 'Exported per-version isolation settings disagree with the option'}
  if((Read-Setting $settings 'Unrelated') -cne 'preserve-source'){throw 'Export changed unrelated version settings'}
  Put-File (Join-Path $pack 'modrinth.index.json') '{"formatVersion":1,"dependencies":{"minecraft":"1.20.1"},"files":[]}'
  $mrpack=Join-Path $fixture ('export-'+$enabled+'.mrpack')
  [IO.Compression.ZipFile]::CreateFromDirectory($pack,$mrpack)
  $archive=[IO.Compression.ZipFile]::OpenRead($mrpack)
  try{$policy=Invoke-Policy 'ReadArchiveChoice' @($archive,'')}finally{$archive.Dispose()}
  if($null -eq $policy){throw 'MRPACK has no explicit isolation choice'}
  if($policy.GetType().GetField('Enabled',$flags).GetValue($policy.PSObject.BaseObject) -ne $enabled){throw 'MRPACK isolation state differs from the export option'}
  $forwardBuffer=[IO.MemoryStream]::new()
  $forwardZip=[IO.Compression.ZipArchive]::new($forwardBuffer,[IO.Compression.ZipArchiveMode]::Create,$true)
  try{$writer=[IO.StreamWriter]::new($forwardZip.CreateEntry('overrides/PCL/Setup.ini').Open());try{$writer.Write([IO.File]::ReadAllText($settings))}finally{$writer.Dispose()}}finally{$forwardZip.Dispose()}
  $forwardBuffer.Position=0
  $forwardZip=[IO.Compression.ZipArchive]::new($forwardBuffer,[IO.Compression.ZipArchiveMode]::Read,$true)
  try{$forwardPolicy=Invoke-Policy 'ReadArchiveChoice' @($forwardZip,'')}finally{$forwardZip.Dispose();$forwardBuffer.Dispose()}
  if($forwardPolicy.GetType().GetField('Enabled',$flags).GetValue($forwardPolicy.PSObject.BaseObject) -ne $enabled){throw 'Forward-slash archive metadata was not recognized'}
  $launcherPack=Join-Path $fixture ('launcher-'+$enabled)
  [IO.Directory]::CreateDirectory($launcherPack)|Out-Null
  [IO.File]::Copy($mrpack,(Join-Path $launcherPack 'modpack.mrpack'))
  $zip=Join-Path $fixture ('launcher-'+$enabled+'.zip')
  [IO.Compression.ZipFile]::CreateFromDirectory($launcherPack,$zip)
  $outer=[IO.Compression.ZipFile]::OpenRead($zip)
  try{
   $stream=$outer.GetEntry('modpack.mrpack').Open()
   $buffer=[IO.MemoryStream]::new()
   try{$stream.CopyTo($buffer);$buffer.Position=0;$inner=[IO.Compression.ZipArchive]::new($buffer,[IO.Compression.ZipArchiveMode]::Read,$true);try{$wrappedPolicy=Invoke-Policy 'ReadArchiveChoice' @($inner,'')}finally{$inner.Dispose()}}finally{$stream.Dispose();$buffer.Dispose()}
  }finally{$outer.Dispose()}
  if($wrappedPolicy.GetType().GetField('Enabled',$flags).GetValue($wrappedPolicy.PSObject.BaseObject) -ne $enabled){throw 'Launcher ZIP lost the explicit isolation choice'}
  $mcRoot=Join-Path $fixture ('import-'+$enabled+'/.minecraft')
  $version=Join-Path $mcRoot 'versions/fixture'
  $versionPath=$version+'\'
  $entries['VersionArgumentIndieV2'].ValueCache[$versionPath]=(-not $enabled)
  Invoke-Policy 'ConfigureTarget' @($version,$policy)|Out-Null
  if($entries['VersionArgumentIndieV2'].ValueCache.ContainsKey($versionPath)){throw 'Old per-version settings cache survived import setup'}
  $gameFolder=Invoke-Policy 'GameFolder' @($version,$mcRoot,$policy)
  if($gameFolder -ne $(if($enabled){$version}else{$mcRoot})){throw 'Download target ignores the isolation choice'}
  Invoke-Policy 'CopyOverrides' @($overrides,$version,$gameFolder,$policy)|Out-Null
  Invoke-Policy 'CopyOverrides' @((Join-Path $pack 'client-overrides'),$version,$gameFolder,$policy)|Out-Null
  if([IO.File]::ReadAllText((Join-Path $gameFolder 'mods/example.jar')) -cne 'synthetic-mod' -or [IO.File]::ReadAllText((Join-Path $gameFolder 'saves/world/level.dat')) -cne 'synthetic-save'){throw 'Imported game files do not match their launch directory'}
  foreach($relative in @('PCL/Logo.png','runtime/jre/bin/java.exe')){if(-not [IO.File]::Exists((Join-Path $version $relative))){throw 'Version configuration or bundled Java escaped the version directory'}}
  if(-not $enabled -and ([IO.Directory]::Exists((Join-Path $version 'mods')) -or [IO.Directory]::Exists((Join-Path $mcRoot 'PCL')) -or [IO.Directory]::Exists((Join-Path $mcRoot 'runtime')))){throw 'Disabled isolation duplicated game files or misplaced version-only data'}
  if((Read-Setting (Join-Path $version 'PCL/Setup.ini') 'VersionArgumentIndieV2') -cne [string]$enabled){throw 'Import overwrote the chosen isolation state'}
  $rejected=$false
  try{Invoke-Policy 'OverrideTarget' @('../escape.txt',$version,$mcRoot,$policy)|Out-Null}catch{$rejected=$true}
  if(-not $rejected){throw 'Override routing accepted a path traversal'}
  if(-not $enabled){
   foreach($reserved in @('versions/other/file.json','libraries/other.jar','assets/indexes/fixture.json')){
    $rejected=$false
    try{Invoke-Policy 'OverrideTarget' @($reserved,$version,$mcRoot,$policy)|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Shared-root import accepted an unrelated version or cache target'}
   }
  }
 }
 $legacyPack=Join-Path $fixture 'legacy'
 Put-File (Join-Path $legacyPack 'overrides/PCL/Setup.ini') "VersionArgumentIndieV2:False`r`n"
 $legacyZip=Join-Path $fixture 'legacy.mrpack'
 [IO.Compression.ZipFile]::CreateFromDirectory($legacyPack,$legacyZip)
 $archive=[IO.Compression.ZipFile]::OpenRead($legacyZip)
 try{$legacyPolicy=Invoke-Policy 'ReadArchiveChoice' @($archive,'')}finally{$archive.Dispose()}
 if($null -ne $legacyPolicy){throw 'A third-party pack was mistaken for an explicit LivingPCL export choice'}
 $legacyVersion=Join-Path $fixture 'legacy-target'
 $asm.GetType('PCL.ModModpack').GetMethod('CopyOverrideDirectory',$flags).Invoke($null,@([string](Join-Path $legacyPack 'overrides'),[string]$legacyVersion,$null,$null,$null))|Out-Null
 if((Read-Setting (Join-Path $legacyVersion 'PCL/Setup.ini') 'VersionArgumentIndieV2') -cne 'True'){throw 'Legacy pack import behavior changed'}
 if([Convert]::ToBase64String([IO.File]::ReadAllBytes($original)) -cne [Convert]::ToBase64String($originalBytes) -or $entries['LaunchArgumentIndieV2'].ValueCache[''] -ne 3){throw 'Export changed the original version or global default isolation'}
 Write-Output 'Export isolation passed: checked default, both states, source/global preservation, MRPACK and launcher ZIP metadata, actual imported files, version-only Java/config, cache invalidation, client overrides, traversal rejection and legacy pack defaults.'
}finally{
 $full=[IO.Path]::GetFullPath($fixture)
 $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-ExportIsolation-[a-f0-9]{32}\z'){throw 'Unsafe export fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
 $app.Shutdown()
}
