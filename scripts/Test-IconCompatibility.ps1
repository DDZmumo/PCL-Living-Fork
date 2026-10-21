param([string]$ExecutablePath,[string]$OfficialExecutablePath,[switch]$ProbeOfficialResources)
# GUID 临时目录；不启动启动器，不访问用户游戏和账号配置。
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell -NoProfile -STA.'}
$root=Split-Path $PSScriptRoot -Parent
$files=@('Anvil.png','CobbleStone.png','CommandBlock.png','Egg.png','Fabric.png','GoldBlock.png','Grass.png','GrassPath.png','NeoForge.png','OptiFabric.png','RedstoneBlock.png','RedstoneLampOff.png','RedstoneLampOn.png')
$officialRoot='pack://application:,,,/Plain Craft Launcher 2;component/Images/'
if($ProbeOfficialResources){
 Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
 $official=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($OfficialExecutablePath))
 if($official.GetName().Name -ne 'Plain Craft Launcher 2'){throw 'Unexpected official assembly identity'}
 [Windows.Application]::ResourceAssembly=$official
 foreach($file in $files){foreach($address in @(($officialRoot+'Blocks/'+$file),('pack://application:,,,/images/Blocks/'+$file))){
  $stream=[Windows.Application]::GetResourceStream([Uri]$address)
  if($null -eq $stream -or $stream.Stream.Length -eq 0){throw "Missing official resource: $file"}
  $stream.Stream.Dispose()
 }}
 Write-Output 'Official resource probe passed: 13 built-in images, official and application-relative pack URIs; entry point not invoked.'
 exit
}
if($OfficialExecutablePath){
 & powershell -NoProfile -STA -File $PSCommandPath -OfficialExecutablePath $OfficialExecutablePath -ProbeOfficialResources
 if($LASTEXITCODE -ne 0){throw 'Official resource probe failed'}
}
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
if($ExecutablePath){
 Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
 foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll','Plain Craft Launcher 2/Resources/ThrottleDebounce.dll')){
  [Reflection.Assembly]::LoadFrom((Join-Path $root $path)) | Out-Null
 }
 $asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
 if($asm.GetName().Name -ne 'LivingPCL'){throw 'LivingPCL must retain its legacy-update-compatible assembly identity'}
 [Windows.Application]::ResourceAssembly=$asm
 $normalize=$asm.GetType('PCL.MyBitmap').GetMethod('NormalizeResourceUri')
 foreach($file in $files){
  $runtime=$normalize.Invoke($null,@($officialRoot+'Blocks/'+$file))
  if($runtime -cne ('pack://application:,,,/LivingPCL;component/Images/Blocks/'+$file)){throw 'Official storage URI was not mapped to LivingPCL for display'}
  $stream=[Windows.Application]::GetResourceStream([Uri]$runtime)
  if($null -eq $stream -or $stream.Stream.Length -eq 0){throw "LivingPCL cannot display the shared built-in icon: $file"}
  $stream.Stream.Dispose()
 }
 $entries=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
 foreach($entry in $entries.Values){$entry.ValueCache['']=$entry.DefaultValue}
}else{
 # 编译实际兼容类；独立 INI 夹具替身不依赖完整启动器构建。
 $source=[IO.File]::ReadAllText((Join-Path $root 'Plain Craft Launcher 2/Modules/Minecraft/MinecraftLogoCompatibility.vb'))
 $stub=@'
Imports System.IO
Public Module ModBase
 Public Function ReadIni(file As String,key As String,Optional fallback As String="") As String
  If Not IO.File.Exists(file) Then Return fallback
  For Each line In IO.File.ReadAllLines(file)
   Dim split=line.IndexOf(":"c)
   If split>=0 AndAlso line.Substring(0,split)=key Then Return line.Substring(split+1)
  Next
  Return fallback
 End Function
 Public Sub WriteIni(file As String,key As String,value As String)
  Dim lines=If(IO.File.Exists(file),IO.File.ReadAllLines(file).ToList(),New List(Of String))
  Dim index=lines.FindIndex(Function(line) line.StartsWith(key & ":",StringComparison.Ordinal))
  If index>=0 Then
   lines(index)=key & ":" & value
  Else
   lines.Add(key & ":" & value)
  End If
  IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(file))
  IO.File.WriteAllLines(file,lines)
 End Sub
 Public Sub IniClearCache(file As String)
 End Sub
End Module
'@
 $options=[Collections.Generic.Dictionary[string,string]]::new()
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
 $compilerPath=(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\vbc.exe' | Select-Object -First 1)
 if(-not $compilerPath -or -not [IO.File]::Exists($compilerPath)){throw 'Visual Studio VB compiler was not found'}
 $options['CompilerDirectoryPath']=[IO.Path]::GetDirectoryName($compilerPath)
 $provider=[Microsoft.VisualBasic.VBCodeProvider]::new($options)
 $parameters=[CodeDom.Compiler.CompilerParameters]::new()
 $parameters.GenerateInMemory=$true
 $parameters.CompilerOptions='/rootnamespace:PCL /optioninfer+ /imports:System,System.Collections.Generic,System.Linq'
 $parameters.ReferencedAssemblies.AddRange([string[]]@('System.dll','System.Core.dll'))
 $compiled=$provider.CompileAssemblyFromSource($parameters,[string[]]@($source,$stub))
 if($compiled.Errors.HasErrors){throw (($compiled.Errors|ForEach-Object {$_.ToString()}) -join "`n")}
 $asm=$compiled.CompiledAssembly
}
$helper=$asm.GetType('PCL.MinecraftLogoCompatibility',$true)
$base=$asm.GetType('PCL.ModBase',$true)
function Invoke-Helper($name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $arguments.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return $helper.GetMethod($name,$flags).Invoke($null,$values)
}
function Clear-Ini([string]$path){$base.GetMethod('IniClearCache',$flags).Invoke($null,@($path))|Out-Null}
function Read-Value([string]$path,[string]$key){return $base.GetMethod('ReadIni',$flags).Invoke($null,@($path,$key,$null))}
function Put-Fixture([string]$path,[string]$value,[string]$custom='True'){
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
 [IO.File]::WriteAllText($path,"Logo:$value`r`nLogoCustom:$custom`r`nUnrelated:preserve`r`n")
 Clear-Ini $path
}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-IconCompat-'+[Guid]::NewGuid().ToString('N'))
try{
 foreach($file in $files){
  $expected=$officialRoot+'Blocks/'+$file
  foreach($value in @(('pack://application:,,,/LivingPCL;component/Images/Blocks/'+$file),('/LivingPCL;component/Images/Blocks/'+$file),('pack://application:,,,/images/Blocks/'+$file),('pack://application:,,,/Plain%20Craft%20Launcher%202;component/Images/Blocks/'+$file),$expected)){
   if((Invoke-Helper 'ToStorageUri' @($value)) -cne $expected){throw "Built-in mapping failed: $file"}
  }
 }
 $customValues=@('PCL\Logo.png','D:\custom\Grass.png','https://example.invalid/Grass.png','pack://application:,,,/Other;component/Images/Blocks/Grass.png','pack://application:,,,/LivingPCL;component/Images/Blocks/Unknown.png','pack://application:,,,/LivingPCL;component/Images/Blocks/../Grass.png','pack://application:,,,/LivingPCL;component/Images/Blocks/Grass.png?custom=1','pack://application:,,,/LivingPCL;component/Images/Heads/Logo.png')
 foreach($value in $customValues){if((Invoke-Helper 'ToStorageUri' @($value)) -cne $value){throw 'Non-built-in address changed'}}
 $setup=Join-Path $fixture 'versions/old/PCL/Setup.ini'
 foreach($custom in @('True','False')){
  Put-Fixture $setup 'pack://application:,,,/LivingPCL;component/Images/Blocks/Fabric.png' $custom
  $read=Invoke-Helper 'ReadVersionLogo' @($setup,'')
  if($read -cne ($officialRoot+'Blocks/Fabric.png') -or (Read-Value $setup 'Logo') -cne $read){throw 'Historical Logo was not repaired'}
  if((Read-Value $setup 'LogoCustom') -cne $custom -or (Read-Value $setup 'Unrelated') -cne 'preserve'){throw 'Repair changed unrelated settings'}
 }
 foreach($value in $customValues){
  Put-Fixture $setup $value
  $before=[IO.File]::ReadAllText($setup)
  if((Invoke-Helper 'ReadVersionLogo' @($setup,'')) -cne $value -or [IO.File]::ReadAllText($setup) -cne $before){throw 'Custom Logo changed during read'}
 }
 $fresh=Join-Path $fixture 'versions/new/PCL/Setup.ini'
 if((Invoke-Helper 'ReadVersionLogo' @($fresh,'fallback')) -cne 'fallback' -or [IO.File]::Exists($fresh)){throw 'Reading a missing Logo created configuration'}
 Invoke-Helper 'WriteVersionLogo' @($fresh,'pack://application:,,,/LivingPCL;component/Images/Blocks/Grass.png')|Out-Null
 if((Read-Value $fresh 'Logo') -cne ($officialRoot+'Blocks/Grass.png')){throw 'First write is incompatible'}
 # 模拟官方原样读写与 LivingPCL 反复交替；中间清空本进程 INI 缓存。
 for($cycle=0;$cycle -lt 6;$cycle++){
  $value=Invoke-Helper 'ReadVersionLogo' @($fresh,'')
  [IO.File]::WriteAllText($fresh,"Logo:$value`r`nLogoCustom:True`r`nUnrelated:preserve`r`n")
  Clear-Ini $fresh
  Invoke-Helper 'WriteVersionLogo' @($fresh,$value)|Out-Null
  if((Read-Value $fresh 'Logo') -cne ($officialRoot+'Blocks/Grass.png')){throw 'Alternating writers changed shared identity'}
 }
 # Automatic disk repair covers every registered root, without loading its versions.
 $sharedA=Join-Path $fixture 'shared-a'
 $sharedB=Join-Path $fixture 'shared-b'
 $brokenA=Join-Path $sharedA 'versions/a/PCL/Setup.ini'
 $brokenB=Join-Path $sharedB 'versions/b/PCL/Setup.ini'
 $unicodeSetup=Join-Path $sharedB 'versions/unicode/PCL/Setup.ini'
 $lockedSetup=Join-Path $sharedA 'versions/locked/PCL/Setup.ini'
 $customSetup=Join-Path $sharedA 'versions/custom/PCL/Setup.ini'
 $badAddress='pack://application:,,,/LivingPCL;component/Images/Blocks/Fabric.png'
 $goodAddress=$officialRoot+'Blocks/Fabric.png'
 Put-Fixture $brokenA $badAddress 'False'
 Put-Fixture $brokenB $badAddress
 Put-Fixture $lockedSetup $badAddress
 Put-Fixture $customSetup 'https://example.invalid/custom.png'
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($unicodeSetup))|Out-Null
 $unicodeText="# comments-preserve`r`nLogo:$badAddress`r`nLogoCustom:True`r`nUnrelated:preserve`r`n"
 [IO.File]::WriteAllText($unicodeSetup,$unicodeText,[Text.Encoding]::Unicode)
 $customBefore=[IO.File]::ReadAllText($customSetup)
 $rootCache=Join-Path $sharedA 'PCL.ini'
 [IO.File]::WriteAllText($rootCache,'CardValue1:preserve')
 $null=Read-Value $brokenA 'Logo'
 $missingRoot=Join-Path $fixture 'missing-root'
 $rootList=[string[]]@($sharedA,$sharedB,$sharedA.ToUpperInvariant(),$missingRoot)
 $locked=[IO.File]::Open($lockedSetup,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{$repaired=Invoke-Helper 'RepairFolders' @(,$rootList)}finally{$locked.Dispose()}
 if($repaired -ne 3){throw 'Automatic repair did not process independent shared roots or skip the locked configuration'}
 foreach($path in @($brokenA,$brokenB,$unicodeSetup)){if((Read-Value $path 'Logo') -cne $goodAddress){throw 'A shared version icon remained incompatible'}}
 if((Read-Value $brokenA 'LogoCustom') -cne 'False' -or [IO.File]::ReadAllText($customSetup) -cne $customBefore -or [IO.File]::ReadAllText($rootCache) -cne 'CardValue1:preserve'){throw 'Automatic repair changed a custom icon or unrelated configuration'}
 if([IO.File]::ReadAllText($unicodeSetup) -cne $unicodeText.Replace($badAddress,$goodAddress) -or [IO.File]::ReadAllBytes($unicodeSetup)[0] -ne 255 -or [IO.File]::ReadAllBytes($unicodeSetup)[1] -ne 254){throw 'Automatic repair changed comments, newlines or UTF-16 encoding'}
 if((Invoke-Helper 'RepairFolders' @(,$rootList)) -ne 1 -or (Invoke-Helper 'RepairFolders' @(,$rootList)) -ne 0){throw 'Automatic repair did not retry a previously locked file or was not idempotent'}
 if([IO.Directory]::Exists($missingRoot) -or @([IO.Directory]::EnumerateFiles($fixture,'*.tmp',[IO.SearchOption]::AllDirectories)).Count){throw 'Automatic repair created directories or left temporary files'}
 if($ExecutablePath){
  $instanceType=$asm.GetType('PCL.ModMinecraft+McInstance',$true)
  $constructor=$instanceType.GetConstructor([type[]]@([string]))
  $instance=$constructor.Invoke([object[]]@([string](Join-Path $fixture 'versions/property')))
  foreach($value in $customValues){
   $instanceType.GetProperty('Logo').SetValue($instance,$value,$null)
   if($instanceType.GetProperty('LogoForStorage',$flags).GetValue($instance,$null) -cne $value){throw 'Runtime mapping leaked into custom storage'}
  }
  $firstFolder=Join-Path $fixture 'first/versions/1.20.1'
  [IO.Directory]::CreateDirectory($firstFolder)|Out-Null
  [IO.File]::WriteAllText((Join-Path $firstFolder '1.20.1.json'),'{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","type":"release","releaseTime":"2023-06-12T13:25:51+00:00","libraries":[]}')
  $first=$constructor.Invoke([object[]]@([string]$firstFolder))
  $null=$instanceType.GetMethod('Load',$flags).Invoke($first,$null)
  $firstLogo=Read-Value (Join-Path $firstFolder 'PCL/Setup.ini') 'Logo'
  if($firstLogo -cne ($officialRoot+'Blocks/Grass.png')){throw 'First real instance load did not write the official built-in Logo'}
  $mcRoot=Join-Path $fixture 'cached'
  $cachedSetup=Join-Path $mcRoot 'versions/cached/PCL/Setup.ini'
  Put-Fixture $cachedSetup 'pack://application:,,,/LivingPCL;component/Images/Blocks/GrassPath.png'
  [IO.File]::AppendAllText($cachedSetup,"State:1`r`nInfo:fixture`r`nVersionVanillaName:1.20.1`r`nReleaseTime:2023-06-12 13:25`r`n")
  Clear-Ini $cachedSetup
  [IO.File]::WriteAllText((Join-Path $mcRoot 'versions/cached/cached.json'),'{"id":"1.20.1","mainClass":"net.minecraft.client.main.Main","type":"release"}')
  $cards=Join-Path $mcRoot 'PCL.ini'
  [IO.File]::WriteAllText($cards,"CardCount:1`r`nCardKey1:3`r`nCardValue1:cached:`r`nVersion:cached`r`nUnrelated:preserve`r`n")
  $before=[IO.File]::ReadAllText($cards)
  $result=$asm.GetType('PCL.ModMinecraft').GetMethod('InitMcInstanceListWithCache',$flags).Invoke($null,@($mcRoot+'\'))
  if($null -eq $result -or $result.Count -ne 1){throw 'Cached version did not load'}
  if((Read-Value $cachedSetup 'Logo') -cne ($officialRoot+'Blocks/GrassPath.png')){throw 'Cache-hit path did not repair the shared Logo'}
  if([IO.File]::ReadAllText($cards) -cne $before){throw 'Card cache or unrelated Minecraft-root settings changed'}
  foreach($value in @('PCL\Logo.png','https://example.invalid/custom.png')){
   Put-Fixture $cachedSetup $value
   [IO.File]::AppendAllText($cachedSetup,"State:1`r`nVersionVanillaName:1.20.1`r`n")
   Clear-Ini $cachedSetup
   $null=$asm.GetType('PCL.ModMinecraft').GetMethod('InitMcInstanceListWithCache',$flags).Invoke($null,@($mcRoot+'\'))
   if((Read-Value $cachedSetup 'Logo') -cne $value -or (Read-Value $cachedSetup 'LogoCustom') -cne 'True'){throw 'Cache read changed a custom Logo'}
  }
 }
 Write-Output 'Icon compatibility passed: 13 built-ins, first write, historical repairs, 8 custom/unknown addresses, LogoCustom preservation, six alternating writes, missing files.'
 Write-Output 'Automatic disk repair passed: independent shared roots, duplicates/missing/locked paths, warm INI cache invalidation, retry/idempotence, comments/newlines/UTF-16 preservation and no unrelated changes.'
 if($ExecutablePath){Write-Output 'Built assembly checks passed: LivingPCL identity, separate runtime/storage addresses, real version-list cache-hit repair, preserved Minecraft-root card cache and custom icons.'}
}finally{
 $full=[IO.Path]::GetFullPath($fixture)
 $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-IconCompat-[a-f0-9]{32}\z'){throw 'Unsafe fixture cleanup'}
 if([IO.Directory]::Exists($full)){
  foreach($path in [IO.Directory]::EnumerateFileSystemEntries($full,'*',[IO.SearchOption]::AllDirectories)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected fixture link'}}
  [IO.Directory]::Delete($full,$true)
 }
}
