param([string]$ExecutablePath,[string]$VbcPath,[string]$ReadOnlyLegacyModsFolder)
# Read-only metadata fixtures in a GUID temp directory. No launcher entry point, game, real profile, or live dependency API.
# Without EXE, compile the full fixed LF50 Inspector/Index reference originals and the existing tiny instance stub.
# Current Core metadata remains a prerequisite guard; its migrated algorithm is not the original reference implementation.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Get-FixedLfSource.ps1')
. (Join-Path $PSScriptRoot 'Resolve-VbCompiler.ps1')
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$jsonPath=Join-Path $root 'Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll'
[Reflection.Assembly]::LoadFrom($jsonPath)|Out-Null
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclModDependencies-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$script:assertions=0
function Assert([bool]$value,[string]$message){$script:assertions++;if(-not $value){throw $message}}
function Folder([string]$name){$path=Join-Path $fixture $name;[IO.Directory]::CreateDirectory($path)|Out-Null;return $path}
function New-Jar([string]$path,[hashtable]$entries,[IO.Compression.CompressionLevel]$level=[IO.Compression.CompressionLevel]::Optimal){
 $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try {
  foreach($name in $entries.Keys){
   $stream=$archive.CreateEntry($name,$level).Open()
   try {
    $value=$entries[$name]
    $bytes=if($value -is [byte[]]){$value}else{[Text.Encoding]::UTF8.GetBytes([string]$value)}
    $stream.Write($bytes,0,$bytes.Length)
   }finally{$stream.Dispose()}
  }
 }finally{$archive.Dispose()}
 return $path
}
function Fabric([string]$folder,[string]$file,[string]$id,[string]$version='1.0',[hashtable]$extra=@{}){
 $data=@{schemaVersion=1;id=$id;name=$id;version=$version}
 foreach($key in $extra.Keys){$data[$key]=$extra[$key]}
 return New-Jar (Join-Path $folder $file) @{'fabric.mod.json'=($data|ConvertTo-Json -Depth 25 -Compress)}
}
function Quilt([string]$folder,[string]$file,[string]$id,[hashtable]$extra=@{},[hashtable]$rootExtra=@{}){
 $loader=@{group='fixture';id=$id;version='1.0';metadata=@{name=$id}}
 foreach($key in $extra.Keys){$loader[$key]=$extra[$key]}
 $data=@{schema_version=1;quilt_loader=$loader}
 foreach($key in $rootExtra.Keys){$data[$key]=$rootExtra[$key]}
 return New-Jar (Join-Path $folder $file) @{'quilt.mod.json'=($data|ConvertTo-Json -Depth 25 -Compress)}
}
# A JVM class file with a genuine class-level runtime @Mod annotation, without executing Java.
function Legacy-Class([hashtable]$values,[string]$descriptor='Lnet/minecraftforge/fml/common/Mod;'){
 $pool=New-Object 'Collections.Generic.List[string]'
 foreach($text in @($descriptor,'RuntimeVisibleAnnotations','fixture/Mod','java/lang/Object')){$pool.Add($text)}
 foreach($key in $values.Keys){$pool.Add([string]$key);$pool.Add([string]$values[$key])}
 $stream=New-Object IO.MemoryStream;$writer=New-Object IO.BinaryWriter($stream)
 function U2([int]$v){$writer.Write([byte](($v -shr 8) -band 255));$writer.Write([byte]($v -band 255))}
 function U4([long]$v){U2 ([int](($v -shr 16) -band 65535));U2 ([int]($v -band 65535))}
 U4 3405691582;U2 0;U2 52;U2 ($pool.Count+3)
 foreach($text in $pool){$bytes=[Text.Encoding]::UTF8.GetBytes($text);$writer.Write([byte]1);U2 $bytes.Length;$writer.Write($bytes)}
 $writer.Write([byte]7);U2 3;$writer.Write([byte]7);U2 4
 U2 33;U2 ($pool.Count+1);U2 ($pool.Count+2);U2 0;U2 0;U2 0;U2 1;U2 2;U4 (6+5*$values.Count)
 U2 1;U2 1;U2 $values.Count
 $index=5;foreach($key in $values.Keys){U2 $index;$writer.Write([byte][char]'s');U2 ($index+1);$index+=2}
 $writer.Flush();$bytes=$stream.ToArray();$writer.Dispose();$stream.Dispose();return ,$bytes
}
try {
 if(-not $ExecutablePath){
  $VbcPath=Resolve-VbCompiler -VbcPath $VbcPath
  # The migrated inspector lives in PCLCS. Build that library and its pinned
  # project dependencies; the main VB launcher and its credentials are not needed.
  $dotnetCommand=(Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
  & $dotnetCommand build (Join-Path $root 'PCLCS/PCLCS.csproj') --configuration Release --verbosity minimal -warnaserror
  if($LASTEXITCODE -ne 0){throw 'Standalone dependency core build failed.'}
  $coreReference=Join-Path $root 'PCLCS/bin/PCLCS.dll'
  $sharedReference=Join-Path $root 'MeloongCore/Shared/bin/MeloongCore.dll'
  foreach($reference in @($coreReference,$sharedReference)){
   if(-not [IO.File]::Exists($reference)){throw ('Missing standalone production dependency: '+$reference)}
  }
  [Reflection.Assembly]::LoadFrom($sharedReference)|Out-Null
  $coreAssembly=[Reflection.Assembly]::LoadFrom($coreReference)
  $coreType=$coreAssembly.GetType('PCLCS.ModDependencyInspection`10',$true)
  if(-not $coreType.IsGenericTypeDefinition -or $coreType.GetGenericArguments().Length -ne 10){throw 'Standalone fixture did not load the migrated production inspector.'}
  $stub=Join-Path $fixture 'InstanceStub.vb'
  [IO.File]::WriteAllText($stub,@'
Imports System.Collections.Generic
Imports Newtonsoft.Json.Linq
Friend Class McInstance
    Public Version As New DependencyFixtureVersion
    Public JsonObject As New JObject
End Class
Friend Class DependencyFixtureVersion
    Public VanillaName As String = "1.20.1"
    Public Reliable As Boolean = True
    Public ModLoaderVersions As New Dictionary(Of String, String)
    Public HasOptiFine As Boolean
    Public OptiFineAsMod As Boolean
    Public OptiFine As String
End Class
'@)
  $ExecutablePath=Join-Path $fixture 'ModDependenciesFixture.dll'
  $framework=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
  $references=@('System.dll','System.Core.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')|ForEach-Object {Join-Path $framework $_}
  $references+=@($coreReference,$sharedReference)
  $arguments=@('/nologo','/target:library','/rootnamespace:PCL','/optioninfer+','/optionstrict+','/utf8output',('/sdkpath:'+$framework),('/out:'+$ExecutablePath),('/reference:'+($references -join ',')),('/reference:'+$jsonPath),(Export-LfFixedSource -Name 'deps-inspector-lf50' -Directory (Join-Path $fixture 'fixed-baseline')),(Export-LfFixedSource -Name 'deps-index-lf50' -Directory (Join-Path $fixture 'fixed-baseline')),$stub)
  & $VbcPath @arguments
  if($LASTEXITCODE -ne 0){throw 'Isolated ModDependencyInspector compilation failed.'}
 }else{
  $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
  foreach($dependency in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll')){
   $dependencyPath=Join-Path $root $dependency
   if([IO.File]::Exists($dependencyPath)){[Reflection.Assembly]::LoadFrom($dependencyPath)|Out-Null}
  }
 }
 # Byte loading leaves the generated DLL removable after the test.
 $asm=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ExecutablePath))
 $inspector=$asm.GetType('PCL.ModDependencyInspector',$true)
 $indexType=$asm.GetType('PCL.ModDependencyIndex')
 Assert ($null -ne $indexType) 'Persistent dependency-tree indexing is not implemented.'
 $indexType.GetProperty('CacheDirectory',$flags).SetValue($null,(Join-Path $fixture 'dependency-index'),$null)
 $contextType=$asm.GetType('PCL.ModDependencyInspector+InspectionContext',$true)
 $inspect=$inspector.GetMethod('Inspect',$flags);$match=$inspector.GetMethod('MatchVersion',$flags)
 $samePath=$inspector.GetMethod('SameFilePath',$flags)
 Assert ([bool]$samePath.Invoke($null,@('\\?\UNC\server\share\mods\example.jar','\\server\share\mods\example.jar'))) 'Extended UNC spelling did not match the same file.'
 Assert (-not [bool]$samePath.Invoke($null,@('\\?\UNC\server\share\mods\example.jar','\\server\other-share\mods\example.jar'))) 'UNC normalization confused different shares.'
 $longPath='C:\'+(('long-folder-'*24))+'\example.jar'
 $key=$inspector.GetMethod('FilePathKey',$flags).Invoke($null,@('\\?\'+$longPath))
 Assert ($key -ceq $longPath) 'Comparing an extended long path lost support beyond MAX_PATH.'
 $getImpacts=$inspector.GetMethod('GetImpacts',$flags);$describeImpacts=$inspector.GetMethod('DescribeImpacts',$flags)
 function Context([string]$loader='fabricloader',[string]$version='0.16.0'){
  $context=[Activator]::CreateInstance($contextType)
  $context.MinecraftVersion=if($loader -eq 'neoforge'){'1.21.1'}else{'1.20.1'};$context.JavaVersion='17.0.9';$context.LoaderKnown=$true
  if($loader){$context.LoaderVersions.Add($loader,$version)}
  return $context
 }
 function Scan([string]$folder,$context=(Context)){
  return $inspect.Invoke($null,@($folder,$context,[Threading.CancellationToken]::None))
 }
 function Issues($report,[string]$kind,[string]$owner=''){
  return @($report.Issues|Where-Object {$_.Kind.ToString() -eq $kind -and (-not $owner -or $_.Owner.Id -eq $owner)})
 }
 function Check-Version([string]$actual,[string]$range,[string]$syntax,[string]$expected){
  $value=$match.Invoke($null,@($actual,$range,$syntax)).ToString()
  Assert ($value -eq $expected) ('Version '+$syntax+': '+$actual+' / '+$range+' expected '+$expected+', got '+$value)
 }
 foreach($case in @(
  @('1.2.3','>=1.2.0 <2.0','Satisfied'),@('1.2.3','>= 1.2.0 < 2.0','Satisfied'),
  @('2.0','>=1.2.0 <2.0','Unsatisfied'),@('1.20.10','1.20.1','Unsatisfied'),
  @('1.2.3','1.2.x','Satisfied'),@('1.3.0','1.2.*','Unsatisfied'),@('1.2.3','~1.2','Satisfied'),@('1.3.0','~1.2','Unsatisfied'),
  @('0.2.9','^0.2.1','Satisfied'),@('0.3','^0.2.1','Satisfied'),@('0.9.0','^0.2.1','Satisfied'),@('1.0.0-alpha','^0.2.1','Unsatisfied'),@('1.0','1.0.0','Satisfied'),
  @('1.0.5','~1','Satisfied'),@('1.1','~1','Unsatisfied'),@('1.3.0-alpha','~1.2','Unsatisfied'),
  @('1.0.0-rc.1','>=1.0.0','Unsatisfied'),@('1.0.0','>1.0.0-rc.1','Satisfied'),
  @('2.0','1.0 || >=2.0','Satisfied'),@('1.5','1.0 || >=2.0','Unsatisfied'),
  @('1.0','latest.release','Unknown'),@('1.0','1.+','Unknown'),@('24w14a','>=1.20.1','Unknown'),
  @('24w14a','24w14a','Satisfied'),@('24w14a','1.20.1','Unsatisfied'),@('1.0','>=v1.0','Unknown'),
  @('unknown','>=1.0','Unknown'),@('1.0','1.0 - 2.0','Unknown')
  ,@('unknown','1.0','Unknown')
 )){Check-Version $case[0] $case[1] 'fabric' $case[2]}
 foreach($case in @(
  @('1.5','[1.0,2.0)','Satisfied'),@('2.0','[1.0,2.0)','Unsatisfied'),@('1.0','(1.0,2.0]','Unsatisfied'),
  @('1.20.1','[1.20.1]','Satisfied'),@('1.20.10','[1.20.1]','Unsatisfied'),
  @('1.0','(,1.0],[2.0,)','Satisfied'),@('1.5','(,1.0],[2.0,)','Unsatisfied'),
  @('1.0-final','[1.0]','Unknown'),@('1.0','[1.0-final]','Unknown'),@('1.0+build.1','(1.0,2.0)','Unknown'),
  @('2.0-beta','[1.0,2.0)','Unknown'),@('2.0','1.0','Unknown'),@('2.0',('[1.0,$'+'{version})'),'Unknown')
 )){Check-Version $case[0] $case[1] 'maven' $case[2]}

 $folder=Folder 'fabric'
 $core=Fabric $folder 'core.jar' 'core' '1.2.0' @{provides=@('api_alias')}
 $addon=Fabric $folder 'addon.jar' 'addon' '1.0' @{depends=@{api_alias=@('>=1.0 <2.0','>=3.0');minecraft='1.20.1';java='>=17';fabricloader='>=0.15'};recommends=@{recommendation='*'};suggests=@{suggested='*'}}
 $consumer=Fabric $folder 'consumer.jar' 'consumer' '1.0' @{depends=@{addon='*'}}
 $null=Fabric $folder 'server.jar' 'server' '1.0' @{environment='server';depends=@{server_only_missing='*'}}
 $disabled=Fabric $folder 'disabled.jar.disabled' 'disabled' '1.0' @{depends=@{disabled_only_missing='*'}}
 $null=Fabric $folder 'legacy.jar.old' 'legacy' '1.0'
 $report=Scan $folder
 Assert (-not $report.HasErrors) 'Required Fabric dependencies should be satisfied.'
 Assert ((Issues $report 'Recommendation').Count -eq 1) 'Recommendations must be warnings.'
 Assert ((Issues $report 'MissingRequired').Count -eq 0) 'Wrong-side and disabled dependencies were checked as active.'
 # The real mod-list UI uses FileUtils.GetInfo, whose FullName has the Windows extended prefix.
 $uiCore='\\?\'+[IO.Path]::GetFullPath($core)
 $uiAddon='\\?\'+[IO.Path]::GetFullPath($addon)
 Assert ($report.DescribeFile($uiAddon) -ceq $report.DescribeFile($addon)) 'UI FileInfo path lost successfully parsed local dependencies.'
 Assert ($report.DescribeFile('\\?\'+[IO.Path]::GetFullPath($disabled)) -ceq $report.DescribeFile($disabled)) 'Extended disabled-file path lost its metadata.'
 $uiImpacts=$getImpacts.Invoke($null,@($report,[string[]]@($uiCore)))
 Assert (@($uiImpacts|Where-Object {$_.Mod.Id -eq 'addon' -and $_.IsDefinite}).Count -eq 1) 'UI FileInfo path bypassed the dependency warning before removing a provider.'
 $extendedReport=Scan ('\\?\'+[IO.Path]::GetFullPath($folder))
 Assert ($extendedReport.DescribeFile($addon) -ceq $report.DescribeFile($addon)) 'An extended scan root could not match an ordinary detail path.'
 $mixedImpacts=$getImpacts.Invoke($null,@($extendedReport,[string[]]@($core,$uiAddon)))
 Assert (@($mixedImpacts|Where-Object {$_.Mod.Id -eq 'consumer'}).Count -eq 1) 'Mixed path forms lost the remaining consumer impact.'
 Assert (@($mixedImpacts|Where-Object {$_.Mod.Id -eq 'addon'}).Count -eq 0) 'A selected owner was incorrectly warned about itself.'
 Assert ($report.Edges.Count -ge 6 -and $report.ReverseDependencies.Count -ge 3) 'Dependency graph or reverse map is empty.'
 $caption=$report.DescribeFile($core)
 Assert ($caption.Contains('addon') -and $caption.Contains('被这些模组依赖')) 'Reverse dependencies are absent from file detail.'
 Assert ($report.ToSummary().Contains('确定问题：0')) 'PCL summary did not include the error count.'
 $impacts=@($getImpacts.Invoke($null,@($report,[string[]]@($core))))
 Assert ($impacts.Count -eq 2) 'Removing the core must affect addon and its transitive consumer.'
 Assert (@($impacts|Where-Object {$_.Mod.Id -eq 'addon' -and $_.IsDirect -and $_.IsDefinite}).Count -eq 1) 'Direct required impact is missing.'
 Assert (@($impacts|Where-Object {$_.Mod.Id -eq 'consumer' -and -not $_.IsDirect}).Count -eq 1) 'Transitive required impact is missing.'
 Assert (@($getImpacts.Invoke($null,@($report,[string[]]@($core,$addon,$consumer)))).Count -eq 0) 'Selected files must not warn as affected by their own batch.'
 Assert ($describeImpacts.Invoke($null,@($report,[string[]]@($core))).Contains('间接受影响')) 'Removal explanation omitted the transitive reason.'
 Assert (@($getImpacts.Invoke($null,@($report,[string[]]@($disabled)))).Count -eq 0) 'Removing a disabled mod should not affect active mods.'
 $before=@{};foreach($path in [IO.Directory]::EnumerateFiles($folder)){$before[$path]=(Get-FileHash -LiteralPath $path).Hash}
 $null=Scan $folder
 foreach($path in $before.Keys){Assert ((Get-FileHash -LiteralPath $path).Hash -ceq $before[$path]) 'Inspection modified a mod.'}
 [IO.File]::Move($core,$core+'.moved');[IO.File]::Move($core+'.moved',$core)

 $folder=Folder 'missing'
 $null=Fabric $folder 'consumer.jar' 'consumer' '1.0' @{depends=@{library='>=1.0'}}
 $null=Fabric $folder 'library.jar.disabled' 'library' '1.5'
 $null=Fabric $folder 'library_extra.jar' 'library_extra' '1.5'
 $report=Scan $folder;$missing=Issues $report 'MissingRequired'
 Assert ($missing.Count -eq 1 -and $missing[0].IsError -and $missing[0].Message.Contains('已禁用')) 'Disabled provider must be a clear missing dependency, without substring matching.'
 $null=Fabric $folder 'duplicate-one.jar' 'duplicate' '1.0'
 $null=Fabric $folder 'duplicate-two.jar' 'duplicate' '2.0'
 $null=Fabric $folder 'duplicate-disabled.jar.disabled' 'duplicate' '3.0'
 $report=Scan $folder;$duplicates=Issues $report 'DuplicateId'
 Assert ($duplicates.Count -eq 1 -and $duplicates[0].RelatedMods.Count -eq 2) 'Duplicate IDs must include only enabled declarations.'

 $folder=Folder 'fabric-id'
 $null=Fabric $folder 'consumer.jar' 'consumer' '1.0' @{depends=@{fabric='*'}}
 Assert ((Issues (Scan $folder) 'MissingRequired').Count -eq 1) 'Fabric API id "fabric" must not resolve to Fabric Loader.'
 $null=Fabric $folder 'fabric-api.jar' 'fabric' '1.0'
 Assert (-not (Scan $folder).HasErrors) 'Exact Fabric API id should resolve.'

 $folder=Folder 'version-errors'
 $null=Fabric $folder 'lib.jar' 'lib' '1.20.10'
 $null=Fabric $folder 'consumer.jar' 'consumer' '1.0' @{depends=@{lib='1.20.1'}}
 $null=Fabric $folder 'unknown.jar' 'unknown' ('$'+'{version}')
 $null=Fabric $folder 'dynamic.jar' 'dynamic' '1.0' @{depends=@{unknown='>=2.0';lib='latest.release'}}
 $report=Scan $folder
 Assert ((Issues $report 'VersionMismatch').Count -eq 1 -and (Issues $report 'VersionMismatch')[0].Owner.Id -eq 'consumer') 'Only definite version mismatches should be errors.'
 $unknownContext=Context;$unknownContext.JavaVersion=$null
 $null=Fabric $folder 'java.jar' 'java_requirement' '1.0' @{depends=@{java='>=21'}}
 $report=Scan $folder $unknownContext
 Assert ((Issues $report 'MissingRequired' 'java_requirement').Count -eq 0 -and (Issues $report 'VersionMismatch' 'java_requirement').Count -eq 0) 'Unknown selected Java version must not be a missing or version error.'
 $report=Scan $folder
 Assert ((Issues $report 'VersionMismatch' 'java_requirement').Count -eq 1) 'Known incompatible Java version should be checked.'

 $folder=Folder 'quilt'
 $null=Quilt $folder 'alias.jar' 'provider' @{provides=@(@{id='fixture:virtual';version='2.5'})}
 $null=Quilt $folder 'consumer.jar' 'quilt_consumer' @{
  depends=@(@{id='fixture:virtual';versions=@{all=@('>=2.0','<3.0')}},@('missing_alternative','fixture:virtual'),@{id='optional_absent';optional=$true},@{id='unless_absent';unless='provider'})
  breaks=@(@{id='provider';versions='*';unless='fixture:virtual'})
 }
 $null=Quilt $folder 'server.jar' 'quilt_server' @{depends=@('server_absent')} @{minecraft=@{environment='dedicated_server'}}
 $report=Scan $folder (Context 'quilt_loader' '0.28.0')
 Assert (-not $report.HasErrors) ('Quilt qualified provides, versions/all, alternative IDs, optional, unless or side handling failed. '+$report.ToSummary())
 $null=Quilt $folder 'bad.jar' 'quilt_bad' @{depends=@(@{id='fixture:virtual';versions=@{any=@('<2.0','>=3.0')}})}
 $report=Scan $folder (Context 'quilt_loader' '0.28.0')
 Assert ((Issues $report 'VersionMismatch' 'quilt_bad').Count -eq 1) 'Quilt any-version mismatch should be definite.'

 $folder=Folder 'forge'
 $toml=@'
modLoader="javafml"
loaderVersion="[47,)"
license="fixture"
[[mods]]
modId="multi_one"
version="FILE_VERSION"
displayName="Multi one"
description='''
[[dependencies.multi_one]]
modId="fake_from_description"
mandatory=true
'''
[[mods]]
modId="multi_two"
version="2.0"
displayName="Name # with hash"
[[dependencies.multi_one]]
modId="multi_two"
mandatory=true
versionRange="[2.0,3.0)" # real comment
side="CLIENT"
[[dependencies.multi_one]]
modId="server_missing"
mandatory=true
side="SERVER"
[[dependencies.multi_one]]
modId="optional_missing"
mandatory=false
versionRange="[1,)"
[[dependencies.multi_two]]
modId="minecraft"
mandatory=true
versionRange="[1.20.1]"
[features.multi_two]
java_version="[17,)"
'@
 $toml=$toml.Replace('FILE_VERSION',('$'+'{file.jarVersion}'))
 $null=New-Jar (Join-Path $folder 'multi.jar') @{'META-INF/mods.toml'=$toml;'META-INF/MANIFEST.MF'="Manifest-Version: 1.0$([Environment]::NewLine)Implementation-Version: 1.2.3$([Environment]::NewLine)"}
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert ($report.Mods.Count -eq 2 -and -not $report.HasErrors) 'Forge multi-mod TOML parsing, multiline quoting, side or optional handling failed.'
 Assert (($report.Mods|Where-Object Id -eq 'multi_one').Version -eq '1.2.3') 'file.jarVersion was not resolved from the manifest.'
 Assert (-not @($report.Edges|Where-Object {$_.Requirement.Id -eq 'fake_from_description'}).Count) 'Description contents became a dependency declaration.'
 $null=New-Jar (Join-Path $folder 'optional.jar') @{'META-INF/mods.toml'="[[mods]]`nmodId=`"optional_library`"`nversion=`"1.0`""}
 $optionalToml=@'
modLoader="javafml"
loaderVersion="[47,)"
[[mods]]
modId="optional_owner"
version="1.0"
[[dependencies.optional_owner]]
modId="optional_library"
mandatory=false
versionRange="[2.0,)"
'@
 $null=New-Jar (Join-Path $folder 'optional-owner.jar') @{'META-INF/mods.toml'=$optionalToml}
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert ((Issues $report 'VersionMismatch' 'optional_owner').Count -eq 1) 'A present incompatible Forge optional dependency must validate its version.'
 $exactLanguage=$optionalToml.Replace('modId="optional_owner"','modId="language_owner"').Replace('dependencies.optional_owner','dependencies.language_owner').Replace('loaderVersion="[47,)"','loaderVersion="[47]"')
 $null=New-Jar (Join-Path $folder 'language.jar') @{'META-INF/mods.toml'=$exactLanguage}
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert (@($report.Issues|Where-Object {$_.Owner.Id -eq 'language_owner' -and $_.Requirement.Id -eq 'javafml'}).Count -eq 0) 'Forge language-loader ranges must compare with FML major, not the full Forge version.'

 $folder=Folder 'neoforge'
 $neo=@'
modLoader="javafml"
loaderVersion="[4,)"
[[mods]]
modId="neo"
version="1.0"
[[dependencies.neo]]
modId="neoforge"
type="required"
versionRange="[21.1,22)"
[[dependencies.neo]]
modId="optional_absent"
type="optional"
[[dependencies.neo]]
modId="bad"
type="incompatible"
versionRange="[1,2)"
'@
 $null=New-Jar (Join-Path $folder 'neo.jar') @{'META-INF/neoforge.mods.toml'=$neo}
 $report=Scan $folder (Context 'neoforge' '21.1.100')
 Assert (-not $report.HasErrors) 'NeoForge language loader version must not be compared with the NeoForge platform version.'
 $null=New-Jar (Join-Path $folder 'bad.jar') @{'META-INF/neoforge.mods.toml'="[[mods]]`nmodId=`"bad`"`nversion=`"1.5`""}
 $report=Scan $folder (Context 'neoforge' '21.1.100')
 Assert ((Issues $report 'Incompatible' 'neo').Count -eq 1) 'NeoForge incompatible dependency should be reported when present and in range.'

 $folder=Folder 'nested'
 $embeddedFolder=Folder 'embedded'
 $nestedFile=Fabric $embeddedFolder 'nested.jar' 'nested_api' '1.0'
 $parentData=@{schemaVersion=1;id='parent';version='1.0';depends=@{nested_api='>=1.0'};jars=@(@{file='META-INF/jars/nested.jar'})}|ConvertTo-Json -Depth 10 -Compress
 $parent=New-Jar (Join-Path $folder 'parent.jar') @{'fabric.mod.json'=$parentData;'META-INF/jars/nested.jar'=[IO.File]::ReadAllBytes($nestedFile)}
 $null=Fabric $folder 'dependent.jar' 'dependent' '1.0' @{depends=@{nested_api='>=1.0'}}
 $report=Scan $folder
 Assert ($report.Mods.Count -eq 3 -and -not $report.HasErrors -and @($report.Mods|Where-Object IsNested).Count -eq 1) 'Declared Fabric nested jar was not read.'
 $impacts=@($getImpacts.Invoke($null,@($report,[string[]]@($parent))))
 Assert ($impacts.Count -eq 1 -and $impacts[0].Mod.Id -eq 'dependent') 'Removing a jar must remove its nested provides from the graph.'
 $null=Fabric $folder 'external.jar' 'nested_api' '2.0'
 $report=Scan $folder
 Assert ((Issues $report 'DuplicateId').Count -eq 0) 'Selectable embedded copies are not duplicate top-level mods.'
 Assert (@($getImpacts.Invoke($null,@($report,[string[]]@($parent)))).Count -eq 0) 'An external fallback should preserve a nested dependency.'
 $quiltNested=Quilt $embeddedFolder 'quilt-inner.jar' 'quilt_inner'
 $quiltData=@{schema_version=1;quilt_loader=@{group='fixture';id='quilt_parent';version='1.0';jars=@('META-INF/jars/quilt.jar');depends=@('quilt_inner')}}|ConvertTo-Json -Depth 10 -Compress
 $null=New-Jar (Join-Path $folder 'quilt-parent.jar') @{'quilt.mod.json'=$quiltData;'META-INF/jars/quilt.jar'=[IO.File]::ReadAllBytes($quiltNested)}
 $report=Scan $folder (Context 'quilt_loader' '0.28')
 Assert (@($report.Mods|Where-Object Id -eq 'quilt_inner').Count -eq 1) 'Quilt string nested-jar declaration was not read.'
 $displayEmbedded=$inspector.GetMethod('EmbeddedModsForDisplay',$flags)
 Assert (@($displayEmbedded.Invoke($null,@($report,[string](Join-Path $folder 'quilt-parent.jar')))).Count -eq 1) 'An independent Quilt nested mod was hidden.'

 # Arbitrary project IDs: display grouping must come from metadata, never a mod-name list.
 $folder=Folder 'embedded-display'
 $parts=@{
  source_part=@{contact=@{sources='https://source.example/team/toolkit.git'}}
  object_part=@{custom=@{modmenu=@{parent=@{id='toolkit_alias'}}}}
  string_part=@{custom=@{modmenu=@{parent='toolkit'}}}
  generated_part=@{custom=@{'fabric-loom:generated'=$true}}
  independent_library=@{contact=@{sources='https://source.example/team/library';homepage='https://team.example'}}
  toolkit_independent=@{contact=@{homepage='https://team.example'}}
  invalid_hint=@{custom=@{modmenu=@{parent=123};'fabric-loom:generated'='true'};contact=@{sources='not a URL'}}
 }
 $entries=@{};$jars=@();$requires=@{}
 foreach($id in $parts.Keys){
  $inner=Fabric $embeddedFolder ($id+'.jar') $id '1.0' $parts[$id]
  $path='META-INF/jars/'+$id+'.jar';$entries[$path]=[IO.File]::ReadAllBytes($inner);$jars+=@{file=$path};$requires[$id]='>=1'
 }
 $entries['fabric.mod.json']=(@{schemaVersion=1;id='toolkit';version='1';provides=@('toolkit_alias');contact=@{sources='https://source.example/team/toolkit/';homepage='https://team.example'};depends=$requires;jars=$jars}|ConvertTo-Json -Depth 15 -Compress)
 $toolkit=New-Jar (Join-Path $folder 'toolkit.jar') $entries
 $report=Scan $folder
 $shown=@($displayEmbedded.Invoke($null,@($report,[string]$toolkit)))
 Assert (-not $report.HasErrors -and $shown.Count -eq 3 -and 'independent_library' -in $shown.Id -and 'toolkit_independent' -in $shown.Id -and 'invalid_hint' -in $shown.Id) 'Internal parents, same-project modules or generated libraries leaked into the embedded display, or an unrelated library was hidden.'
 Assert (@($report.Mods|Where-Object {$_.Id -eq 'generated_part' -and $_.IsGeneratedLibrary}).Count -eq 1 -and @($report.Mods|Where-Object {$_.Id -eq 'object_part' -and $_.DisplayParentId -eq 'toolkit_alias'}).Count -eq 1) 'Display hints were not parsed from actual nested metadata.'
 $indexType.GetMethod('ClearMemory',$flags).Invoke($null,@())|Out-Null
 $cached=Scan $folder
 Assert ($cached.GetType().GetField('IndexHit',$flags).GetValue($cached) -and @($displayEmbedded.Invoke($null,@($cached,[string]$toolkit))).Count -eq 3) 'Persistent indexes lost embedded display ownership.'
 # Hiding internal components is presentation only: their dependency errors remain real.
 $null=Fabric $folder 'bad-consumer.jar' 'bad_consumer' '1' @{depends=@{source_part='>=2'}}
 $broken=Scan $folder
 Assert ($broken.HasErrors -and (Issues $broken 'VersionMismatch' 'bad_consumer').Count -eq 1) 'A grouped internal module stopped participating in version checks.'

 $folder=Folder 'unused-nested'
 $unusedFile=Fabric $embeddedFolder 'unused.jar' 'unused' '1.0' @{depends=@{candidate_missing='*'}}
 $unusedData=@{schemaVersion=1;id='candidate_parent';version='1.0';breaks=@{unused='*'};jars=@(@{file='META-INF/jars/unused.jar'})}|ConvertTo-Json -Depth 10 -Compress
 $unusedParent=New-Jar (Join-Path $folder 'parent.jar') @{'fabric.mod.json'=$unusedData;'META-INF/jars/unused.jar'=[IO.File]::ReadAllBytes($unusedFile)}
 $report=Scan $folder
 Assert (-not $report.HasErrors -and @($report.Mods|Where-Object {$_.Id -eq 'unused' -and $_.IsCandidate}).Count -eq 1) 'An unrequired nested candidate must not cause a definite missing or incompatibility error.'

 $folder=Folder 'jarjar'
 $innerToml=@'
modLoader="javafml"
loaderVersion="[4,)"
[[mods]]
modId="jarjar_library"
version="1.0"
'@
 $inner=New-Jar (Join-Path $embeddedFolder 'jarjar.jar') @{'META-INF/neoforge.mods.toml'=$innerToml}
 $outerNeo=$neo.Replace('modId="bad"','modId="jarjar_library"').Replace('type="incompatible"','type="required"')
 $null=New-Jar (Join-Path $folder 'outer.jar') @{'META-INF/neoforge.mods.toml'=$outerNeo;'META-INF/jarjar/metadata.json'='{"jars":[{"path":"META-INF/jarjar/library.jar"}]}';'META-INF/jarjar/library.jar'=[IO.File]::ReadAllBytes($inner)}
 $report=Scan $folder (Context 'neoforge' '21.1.1')
 Assert (-not $report.HasErrors -and @($report.Mods|Where-Object Id -eq 'jarjar_library').Count -eq 1) 'NeoForge JarJar metadata was not read.'
 Assert (@($displayEmbedded.Invoke($null,@($report,[string](Join-Path $folder 'outer.jar')))).Count -eq 1) 'A real NeoForge JarJar mod was hidden with loader internals.'
 $folder=Folder 'forge-jarjar-display'
 $forgeInner=New-Jar (Join-Path $embeddedFolder 'forge-display.jar') @{'META-INF/mods.toml'=$innerToml.Replace('[4,)','[47,)')}
 $forgeOuter=$outerNeo.Replace('modId="neo"','modId="forge_host"').Replace('dependencies.neo','dependencies.forge_host').Replace('modId="neoforge"','modId="forge"').Replace('[21.1,22)','[47,)').Replace('[4,)','[47,)')
 $forgeJar=New-Jar (Join-Path $folder 'outer.jar') @{'META-INF/mods.toml'=$forgeOuter;'META-INF/jarjar/metadata.json'='{"jars":[{"path":"META-INF/jarjar/library.jar"}]}';'META-INF/jarjar/library.jar'=[IO.File]::ReadAllBytes($forgeInner)}
 $report=Scan $folder (Context 'forge' '47.1.0')
 Assert (@($displayEmbedded.Invoke($null,@($report,[string]$forgeJar))).Count -eq 1 -and @($report.Mods|Where-Object IsNested).Count -eq 1) 'A real Forge JarJar mod was hidden or a language-loader component was listed as an embedded mod.'

 $folder=Folder 'uncertain'
 $null=Fabric $folder 'consumer.jar' 'consumer' '1.0' @{depends=@{missing='*'}}
 $null=New-Jar (Join-Path $folder 'unknown.jar') @{'fixture.txt'='not a recognised metadata format'}
 $report=Scan $folder
 Assert ($report.MetadataIncomplete -and -not $report.HasErrors -and (Issues $report 'MissingRequired').Count -eq 1) 'Unread metadata must downgrade missing dependency certainty.'
 [IO.File]::WriteAllText((Join-Path $folder 'corrupt.jar'),'not a ZIP')
 $report=Scan $folder
 Assert ($report.MetadataIncomplete -and -not $report.HasErrors) 'A corrupt jar must be explained without a false definite missing error.'
 $null=New-Jar (Join-Path $folder 'invalid-utf8.jar') @{'fabric.mod.json'=[byte[]]@(255,255)}
 $report=Scan $folder
 Assert ($report.MetadataIncomplete -and -not $report.HasErrors) 'Invalid UTF-8 metadata must not crash the scan.'
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel();$rejected=$false
 try{$inspect.Invoke($null,@([string]$folder,(Context),$cancel.Token))|Out-Null}catch{$reason=$_.Exception;while($reason.InnerException){$reason=$reason.InnerException};$rejected=$reason -is [OperationCanceledException]}finally{$cancel.Dispose()}
 Assert $rejected ('Cancellation was not honoured. '+[string]$reason)

 $folder=Folder 'loader-mismatch'
 $foreignToml='modLoader="javafml"'+"`n"+'loaderVersion="[47,)"'+"`n"+'[[mods]]'+"`n"+'modId="foreign"'+"`n"+'version="1"'+"`n"+'[[dependencies.foreign]]'+"`n"+'modId="foreign_only_dependency"'+"`n"+'mandatory=true'
 $foreign=New-Jar (Join-Path $folder 'forge-only.jar') @{'META-INF/mods.toml'=$foreignToml}
 $null=New-Jar (Join-Path $folder 'neo-only.jar') @{'META-INF/neoforge.mods.toml'=$foreignToml.Replace('foreign','neo_foreign')}
 $null=New-Jar (Join-Path $folder 'disabled.jar.disabled') @{'META-INF/mods.toml'=$foreignToml}
 $null=New-Jar (Join-Path $folder 'universal.jar') @{'META-INF/mods.toml'=$foreignToml;'fabric.mod.json'='{"schemaVersion":1,"id":"universal","version":"1"}'}
 $report=Scan $folder
 Assert ((Issues $report 'LoaderMismatch').Count -eq 2 -and (Issues $report 'MissingRequired').Count -eq 0 -and (Issues $report 'DuplicateId').Count -eq 0) 'Foreign Forge/NeoForge files were not isolated from a Fabric dependency graph, or disabled/universal files were falsely flagged.'
 Assert ((Issues $report 'LoaderMismatch')[0].Message.Contains('禁用') -and (Issues $report 'LoaderMismatch')[0].Message.Contains('Fabric')) 'Wrong-loader warnings do not identify the active loader and a concrete solution.'
 $warning=$inspector.GetMethod('LoaderWarningFor',$flags)
 Assert (-not [string]::IsNullOrEmpty($warning.Invoke($null,@([string]$folder,[string]$foreign,(Context))))) 'A completed inspection did not publish the wrong-loader list marker.'
 Assert ([string]::IsNullOrEmpty($warning.Invoke($null,@([string]$folder,[string]$foreign,(Context 'forge' '47.2.0'))))) 'A marker from the previous loader leaked into the new instance context.'
 $oldBytes=[IO.File]::ReadAllBytes($foreign);[IO.File]::WriteAllBytes($foreign,([byte[]]($oldBytes+0)))
 Assert ([string]::IsNullOrEmpty($warning.Invoke($null,@([string]$folder,[string]$foreign,(Context))))) 'Replacing a mod kept a stale red marker.'
 [IO.File]::WriteAllBytes($foreign,$oldBytes);$report=Scan $folder
 $requirement=[Activator]::CreateInstance($asm.GetType('PCL.ModDependencyInspector+Dependency'));$requirement.Id='foreign'
 Assert (-not $inspector.GetMethod('FileSatisfiesDependency',$flags).Invoke($null,@($report,$foreign,$requirement))) 'An incompatible-loader file can be auto-enabled as a dependency.'
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert (@(Issues $report 'LoaderMismatch' 'neo_foreign').Count -eq 1 -and @(Issues $report 'LoaderMismatch' 'foreign').Count -eq 0) 'Forge and NeoForge mismatch detection failed.'
 $folder=Folder 'quilt-only';$null=Quilt $folder 'quilt.jar' 'quilt_only'
 Assert ((Issues (Scan $folder) 'LoaderMismatch').Count -eq 1 -and (Issues (Scan $folder (Context 'quilt_loader' '0.27.0')) 'LoaderMismatch').Count -eq 0) 'Quilt-only metadata was incorrectly accepted as Fabric.'
 Assert ((Issues (Scan $folder (Context '' '')) 'LoaderMismatch').Count -eq 0) 'An unknown installed loader produced a definite mismatch.'

 $folder=Folder 'forge-neo-platform';$platformToml="[[mods]]`nmodId=`"platform_owner`"`nversion=`"1`"`n[[dependencies.platform_owner]]`nmodId=`"forge`"`nmandatory=true`nversionRange=`"[47,)`""
 $null=New-Jar (Join-Path $folder 'forge.jar') @{'META-INF/mods.toml'=$platformToml}
 $null=New-Jar (Join-Path $folder 'old-neo.jar') @{'META-INF/mods.toml'=$platformToml.Replace('platform_owner','neo_owner').Replace('modId="forge"','modId="neoforge"').Replace('[47,)','[20,)')}
 $neoContext=Context 'neoforge' '20.4.200';$neoContext.MinecraftVersion='1.20.4';$report=Scan $folder $neoContext
 Assert ((Issues $report 'LoaderMismatch' 'platform_owner').Count -eq 1 -and (Issues $report 'LoaderMismatch' 'neo_owner').Count -eq 0) 'Legacy NeoForge mods.toml was confused with a Forge-only mod.'
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert ((Issues $report 'LoaderMismatch' 'neo_owner').Count -eq 1 -and (Issues $report 'LoaderMismatch' 'platform_owner').Count -eq 0) 'A NeoForge declaration in mods.toml was accepted by Forge.'
 $folder=Folder 'loader-list-trailing';$foreign=New-Jar (Join-Path $folder 'forge.jar') @{'META-INF/mods.toml'=$platformToml}
 $null=Scan ($folder+'\')
 Assert (-not [string]::IsNullOrEmpty($warning.Invoke($null,@([string]$folder,[string]$foreign,(Context))))) 'A trailing separator in the real mod-list loader hid its red marker.'

 $folder=Folder 'fabric-schema-zero'
 $null=New-Jar (Join-Path $folder 'old.jar') @{'fabric.mod.json'='{"id":"old_fabric","version":"1","side":"universal","requires":{"old_missing":[">=1 <2",">=3"]},"recommends":{"ignored_suggestion":"*"}}'}
 $null=New-Jar (Join-Path $folder 'server.jar') @{'fabric.mod.json'='{"schemaVersion":0,"id":"old_server","version":"1","side":"server","requires":{"server_missing":"*"}}'}
 $report=Scan $folder
 Assert ((Issues $report 'MissingRequired' 'old_fabric').Count -eq 1 -and (Issues $report 'MissingRequired' 'old_server').Count -eq 0 -and -not $report.MetadataIncomplete) 'Fabric schema 0 requires/side was not read using its own schema.'
 $null=Fabric $folder 'old-target.jar' 'old_missing' '1.5';$report=Scan $folder
 Assert (-not $report.HasErrors -and (Issues $report 'Recommendation').Count -eq 0) 'Fabric schema 0 OR ranges or informational recommendations were misclassified.'

 $folder=Folder 'forge-file-properties'
 $propsToml="modLoader=`"javafml`"`nloaderVersion=`"[47,)`"`n[[mods]]`nmodId=`"props_library`"`nversion=`"`${file.libraryVersion}`"`n[properties]`nlibraryVersion=`"1.2.3`""
 $null=New-Jar (Join-Path $folder 'properties.jar') @{'META-INF/mods.toml'=$propsToml}
 $report=Scan $folder (Context 'forge' '47.2.0')
 Assert ($report.Mods[0].Version -eq '1.2.3') 'Forge file properties were not substituted into the real mod version.'

 $folder=Folder 'neo-metadata-generations';$oldNeo=$platformToml.Replace('platform_owner','neo_owner').Replace('modId="forge"','modId="neoforge"').Replace('[47,)','[20,)')
 $null=New-Jar (Join-Path $folder 'old-format.jar') @{'META-INF/mods.toml'=$oldNeo}
 $null=New-Jar (Join-Path $folder 'new-format.jar') @{'META-INF/neoforge.mods.toml'=$oldNeo}
 $report=Scan $folder (Context 'neoforge' '21.1.100')
 Assert ((Issues $report 'LoaderMismatch').Count -eq 1 -and [IO.Path]::GetFileName((Issues $report 'LoaderMismatch')[0].Owner.RootPath) -eq 'old-format.jar') 'Current NeoForge silently accepted an unsupported old mods.toml file.'
 $report=Scan $folder $neoContext
 Assert ((Issues $report 'LoaderMismatch').Count -eq 1 -and [IO.Path]::GetFileName((Issues $report 'LoaderMismatch')[0].Owner.RootPath) -eq 'new-format.jar') 'Legacy NeoForge silently accepted a future neoforge.mods.toml format.'
 $folder=Folder 'neo-dual-generations';$null=New-Jar (Join-Path $folder 'dual.jar') @{'META-INF/mods.toml'=$oldNeo;'META-INF/neoforge.mods.toml'=$oldNeo}
 Assert (-not (Scan $folder $neoContext).HasErrors -and -not (Scan $folder (Context 'neoforge' '21.1.100')).HasErrors) 'NeoForge did not choose the declaration matching its metadata generation.'

 $folder=Folder 'three-loader-matrix'
 $null=Fabric $folder 'fabric.jar' 'fabric_matrix' '1' @{depends=@{fabricloader='>=0.15'}}
 $null=New-Jar (Join-Path $folder 'forge.jar') @{'META-INF/mods.toml'=$platformToml.Replace('[47,)','[52,)')}
 $null=New-Jar (Join-Path $folder 'neo.jar') @{'META-INF/neoforge.mods.toml'=$oldNeo}
 foreach($loader in @('fabricloader','forge','neoforge')){
  $matrixContext=Context $loader $(if($loader -eq 'fabricloader'){'0.16.10'}elseif($loader -eq 'forge'){'52.0.0'}else{'21.1.100'});$matrixContext.MinecraftVersion='1.21.1'
  $report=Scan $folder $matrixContext
  Assert ((Issues $report 'LoaderMismatch').Count -eq 2 -and (Issues $report 'MissingRequired').Count -eq 0 -and (Issues $report 'VersionMismatch').Count -eq 0) ('Three-loader compatibility matrix failed for '+$loader)
 }

 $folder=Folder 'legacy-annotations';$legacyContext=Context 'forge' '14.23.5.2855';$legacyContext.MinecraftVersion='1.12.2'
 $modernTemplate='modLoader="javafml"'+"`n"+'loaderVersion="[31,)"'+"`n"+'[[mods]]'+"`n"+'modId="examplemod"'+"`n"+'version="1"'
 $null=New-Jar (Join-Path $folder 'scaling.jar') @{'mcmod.info'='[{"modid":"bountiful","version":"1","requiredMods":["MinecraftForge,missing_template"]}]';'META-INF/mods.toml'=$modernTemplate;'fixture/Mod.class'=(Legacy-Class @{modid='scalingwealth';name='Scaling Wealth';version='1.0.1';dependencies='required-after:forge@[14.21.1.2443,)';acceptedMinecraftVersions='[1.12]'})}
 $null=New-Jar (Join-Path $folder 'bountiful.jar') @{'mcmod.info'='[{"modid":"bountiful","version":"2"}]';'fixture/Mod.class'=(Legacy-Class @{modid='bountiful';name='Bountiful';version='2.2.2'})}
 $report=Scan $folder $legacyContext
 Assert (@($report.Mods|Where-Object Id -eq 'scalingwealth').Count -eq 1 -and -not $report.Issues.Count) 'Legacy Forge selected modern TOML/template mcmod.info instead of the actual @Mod ID and dependency declaration.'
 $null=New-Jar (Join-Path $folder 'annotation-only.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='bare_legacy';version='1'})}
 Assert ((Issues (Scan $folder) 'LoaderMismatch').Count -eq 3) 'Legacy Forge @Mod jars, including annotation-only files, were not detected in a Fabric instance.'
 $null=New-Jar (Join-Path $folder 'scala-mirror.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='scala_fixture';version='1'});'fixture/Mod$.class'=(Legacy-Class @{modid='scala_fixture';version='1'});'fixture/Mod 2.class'=(Legacy-Class @{modid='scala_fixture';version='1'});'__MACOSX/Mod.class'=(Legacy-Class @{modid='scala_fixture';version='1'})}
 Assert ((Issues (Scan $folder $legacyContext) 'DuplicateId').Count -eq 0) 'Scala companion classes, whitespace copies or __MACOSX entries became duplicate mods.'
 $null=New-Jar (Join-Path $folder 'requires.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='actual_owner';version='1';dependencies='required-client-after:actual_missing@[1,2);after:absent_optional@[2,);required-server:server_only';acceptedMinecraftVersions='[1.12.2]'})}
 $report=Scan $folder $legacyContext
 Assert ((Issues $report 'MissingRequired' 'actual_owner').Count -eq 1 -and (Issues $report 'MissingRequired' 'actual_owner')[0].Requirement.Id -eq 'actual_missing') 'Real legacy required annotations were lost, or optional/server dependencies were treated as required.'
 $null=New-Jar (Join-Path $folder 'duplicate.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='scalingwealth';version='1'})}
 Assert ((Issues (Scan $folder $legacyContext) 'DuplicateId').Count -eq 1) 'A real duplicate legacy @Mod ID was suppressed.'

 $folder=Folder 'legacy-contained';$embeddedFolder=Folder 'legacy-embedded'
 $inner=New-Jar (Join-Path $embeddedFolder 'contained.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='legacy_inner';version='1.1'})}
 $outer=New-Jar (Join-Path $folder 'outer.jar') @{'META-INF/MANIFEST.MF'="Manifest-Version: 1.0`r`nContainedDeps: contained.jar`r`n`r`n";'META-INF/libraries/contained.jar'=[IO.File]::ReadAllBytes($inner);'fixture/Mod.class'=(Legacy-Class @{modid='legacy_outer';version='1';dependencies='required-after:legacy_inner@[1,2)'})}
 $report=Scan $folder $legacyContext
 Assert ($report.Mods.Count -eq 2 -and -not $report.Issues.Count -and -not $report.MetadataIncomplete) 'Declared legacy ContainedDeps under META-INF/libraries did not supply a required dependency.'
 $report.Mods[0].Name='mutated fixture';$cached=Scan $folder $legacyContext
 Assert ($cached.Mods[0].Name -ne 'mutated fixture' -and -not $cached.Issues.Count) 'Legacy scan cache leaked mutable report objects.'
 [IO.File]::Delete($outer);$null=New-Jar $outer @{'fixture/Mod.class'=(Legacy-Class @{modid='legacy_outer';version='2';dependencies='required-after:truly_missing'})}
 Assert ((Issues (Scan $folder $legacyContext) 'MissingRequired').Count -eq 1) 'Changing a legacy JAR reused stale dependency metadata.'

 $folder=Folder 'legacy'
 $null=New-Jar (Join-Path $folder 'legacy.jar') @{'mcmod.info'='[{"modid":"legacy","name":"Legacy","version":"1.0","requiredMods":["old_library@[1,2)"]}]'}
 $report=Scan $folder (Context 'forge' '14.23')
 Assert ($report.Mods.Count -eq 1 -and $report.MetadataIncomplete -and -not $report.HasErrors) 'Legacy Forge declarations must remain conservative about annotation-only dependencies.'
 $versionFolder=Join-Path $folder '1.12.2';[IO.Directory]::CreateDirectory($versionFolder)|Out-Null
 $null=New-Jar (Join-Path $versionFolder 'legacy-folder.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='version_folder';version='1'})}
 $context=Context 'forge' '14.23';$context.LegacyForgeVersionFolder='1.12.2'
 Assert ((Scan $folder $context).Mods.Count -eq 2) 'Explicit legacy Forge version folder was not included.'

 # Only the standalone adapter is created here. A real McInstance constructor can consult profile settings.
 if($asm.GetType('PCL.DependencyFixtureVersion')){
  $instance=[Activator]::CreateInstance($asm.GetType('PCL.McInstance'))
  $instance.Version.ModLoaderVersions.Add('Fabric','0.27.1')
  $instance.JsonObject=[Newtonsoft.Json.Linq.JObject]::Parse('{"libraries":[{"name":"org.quiltmc:quilt-loader:0.27.1"}]}')
  $factory=$inspector.GetMethod('ContextForInstance',$flags).Invoke($null,@($instance,[string]'1.8.0_402'))
  Assert ($factory.LoaderKnown -and $factory.LoaderVersions['quilt_loader'] -eq '0.27.1' -and $factory.LoaderVersions.ContainsKey('fabricloader') -and -not $factory.LoaderVersions['fabricloader']) 'Quilt must not use its loader version as the Fabric compatibility version.'
  $instance.Version.ModLoaderVersions.Clear();$instance.Version.ModLoaderVersions.Add('Forge','14.23.5');$instance.Version.VanillaName='1.12.2';$instance.JsonObject=[Newtonsoft.Json.Linq.JObject]::Parse('{}')
  $factory=$inspector.GetMethod('ContextForInstance',$flags).Invoke($null,@($instance,[string]'17.0.1'))
  Assert ($factory.LoaderVersions['forge'] -eq '14.23.5' -and $factory.LegacyForgeVersionFolder -eq '1.12.2') 'Instance loader or legacy-folder mapping failed.'
  $instance.Version.ModLoaderVersions.Clear();$instance.Version.ModLoaderVersions.Add('NeoForge','21.1.100');$instance.Version.VanillaName='1.21.1';$instance.JsonObject=[Newtonsoft.Json.Linq.JObject]::Parse('{"libraries":[{"name":"net.neoforged.fancymodloader:loader:4.0.42"}]}')
  $factory=$inspector.GetMethod('ContextForInstance',$flags).Invoke($null,@($instance,[string]'21.0.1'))
  Assert ($factory.AdditionalBuiltins['javafml'] -eq '4.0.42') 'NeoForge Java FML was not derived from the actual FML library version.'
  $instance.Version.Reliable=$false
  $factory=$inspector.GetMethod('ContextForInstance',$flags).Invoke($null,@($instance,$null))
  Assert (-not $factory.MinecraftVersion) 'A guessed Minecraft version must not produce definite version mismatches.'
 }
 # Full graph snapshots must survive process restart without reopening any unchanged JAR.
 $folder=Folder 'persistent-tree';$indexContext=Context
 $core=Fabric $folder 'cached-core.jar' 'cached_core' '1.0'
 $consumer=Fabric $folder 'cached-user.jar' 'cached_user' '1.0' @{depends=@{cached_core='>=1';java='>=21'}}
 $first=Scan $folder $indexContext
 function Index-Flag($value,[string]$name){return $value.GetType().GetField($name,$flags).GetValue($value)}
 Assert (-not (Index-Flag $first 'IndexHit') -and (Index-Flag $first 'IndexPersisted')) 'First inspection did not persist a new dependency tree.'
 $indexPath=$indexType.GetMethod('IndexPath',$flags).Invoke($null,@([string]$folder,$indexContext))
 $lease=[IO.File]::Open($consumer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
 try{
  $indexType.GetMethod('ClearMemory',$flags).Invoke($null,@())|Out-Null
  $second=Scan $folder $indexContext
  Assert ((Index-Flag $second 'IndexHit') -and $second.Mods.Count -eq 2) 'Disk tree reuse reopened an exclusively locked, unchanged JAR.'
  $edge=$second.Edges|Where-Object {$_.Requirement.Id -eq 'cached_core'}|Select-Object -First 1
  Assert ([Object]::ReferenceEquals($edge.Owner,($second.Mods|Where-Object Id -eq 'cached_user')) -and [Object]::ReferenceEquals($edge.Requirement,($edge.Owner.Dependencies|Where-Object Id -eq 'cached_core'))) 'Persistent tree lost graph identity, breaking dependency/reverse relations.'
  $newRuntime=Context;$newRuntime.JavaVersion='21.0.2';$third=Scan $folder $newRuntime
  Assert ((Index-Flag $third 'IndexHit') -and (Index-Flag $third 'IndexReevaluated') -and -not $third.HasErrors) 'Changing Java reread JARs or retained the previous runtime errors.'
  $child=Join-Path $fixture 'ReadIndex.ps1'
  [IO.File]::WriteAllText($child,@'
param($Executable,$Project,$Mods,$Cache)
$ErrorActionPreference='Stop'
foreach($file in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){if(Test-Path -LiteralPath (Join-Path $Project $file)){[Reflection.Assembly]::LoadFrom((Join-Path $Project $file))|Out-Null}}
$a=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable));$f=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$a.GetType('PCL.ModDependencyIndex').GetProperty('CacheDirectory',$f).SetValue($null,$Cache,$null)
$c=[Activator]::CreateInstance($a.GetType('PCL.ModDependencyInspector+InspectionContext'));$c.MinecraftVersion='1.20.1';$c.JavaVersion='21.0.2';$c.LoaderKnown=$true;$c.LoaderVersions.Add('fabricloader','0.16.0')
$r=$a.GetType('PCL.ModDependencyInspector').GetMethod('Inspect',$f).Invoke($null,@([string]$Mods,$c,[Threading.CancellationToken]::None))
if(-not $r.GetType().GetField('IndexHit',$f).GetValue($r) -or $r.HasErrors -or $r.Mods.Count -ne 2){throw 'Restart did not restore the persistent tree'}
Write-Output 'PASS: a fresh process reused the disk tree while its mod JAR was exclusively locked.'
'@,[Text.UTF8Encoding]::new($true))
  & powershell -NoProfile -File $child -Executable $ExecutablePath -Project $root -Mods $folder -Cache (Join-Path $fixture 'dependency-index')
  Assert ($LASTEXITCODE -eq 0) 'A fresh launcher process rebuilt an unchanged dependency tree.'
 }finally{$lease.Dispose()}
 $first.Mods[0].Name='mutated report';$fresh=Scan $folder $newRuntime
 Assert ($fresh.Mods[0].Name -ne 'mutated report') 'A caller mutation corrupted the cached tree.'
 [IO.File]::Move($core,$core+'.disabled');$disabled=Scan $folder $newRuntime
 Assert (-not (Index-Flag $disabled 'IndexHit') -and (Issues $disabled 'MissingRequired').Count -eq 1) 'Disabling a mod reused a stale graph.'
 [IO.File]::Move($core+'.disabled',$core);$enabled=Scan $folder $newRuntime
 Assert (-not (Index-Flag $enabled 'IndexHit') -and -not $enabled.HasErrors) 'Re-enabling a mod did not update the tree.'
 $added=Fabric $folder 'new-mod.jar' 'new_mod';$expanded=Scan $folder $newRuntime
 Assert (-not (Index-Flag $expanded 'IndexHit') -and $expanded.Mods.Count -eq 3) 'Adding a mod did not invalidate the tree.'
 [IO.File]::Delete($added);$enabled=Scan $folder $newRuntime
 Assert (-not (Index-Flag $enabled 'IndexHit') -and $enabled.Mods.Count -eq 2) 'Removing a mod did not invalidate the tree.'
 $generation=Index-Flag $enabled 'IndexGeneration'
 $forced=$inspector.GetMethod('RebuildIndex',$flags).Invoke($null,@([string]$folder,$newRuntime,[Threading.CancellationToken]::None))
 Assert (-not (Index-Flag $forced 'IndexHit') -and (Index-Flag $forced 'IndexGeneration') -ne $generation) 'Manual index update reused the old snapshot.'
 [IO.File]::WriteAllText($indexPath,'{broken snapshot');$indexType.GetMethod('ClearMemory',$flags).Invoke($null,@())|Out-Null
 Assert (-not (Index-Flag (Scan $folder $newRuntime) 'IndexHit')) 'A corrupt index was not automatically rebuilt.'
 $document=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText($indexPath));$document['Tree']['Mods']=[Newtonsoft.Json.Linq.JValue]::CreateNull();[IO.File]::WriteAllText($indexPath,$document.ToString())
 $indexType.GetMethod('ClearMemory',$flags).Invoke($null,@())|Out-Null
 Assert (-not (Index-Flag (Scan $folder $newRuntime) 'IndexHit')) 'A valid JSON index with a broken graph was trusted.'
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel();$cancelled=$false
 try{$inspect.Invoke($null,@([string]$folder,$newRuntime,$cancel.Token))|Out-Null}catch{$e=$_.Exception;while($e.InnerException){$e=$e.InnerException};$cancelled=$e -is [OperationCanceledException]}finally{$cancel.Dispose()}
 Assert $cancelled 'Cache hits ignored a cancelled dependency check.'

 $lockedFolder=Folder 'first-index-locked';$lockedJar=Fabric $lockedFolder 'locked.jar' 'locked_mod';$lease=[IO.File]::Open($lockedJar,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
 try{$partial=Scan $lockedFolder;$failedRead=Index-Flag $partial 'IndexReadFailed';Assert ($failedRead -and -not (Index-Flag $partial 'IndexPersisted')) 'A transient file-read failure was committed as a complete dependency tree.'}finally{$lease.Dispose()}
 $recovered=Scan $lockedFolder
 Assert (-not (Index-Flag $recovered 'IndexHit') -and (Index-Flag $recovered 'IndexPersisted') -and $recovered.Mods.Count -eq 1) 'Releasing an unchanged JAR did not recover the failed initial index.'
 $legacyFolder=Folder 'manual-legacy-index';$packages=Folder 'manual-legacy-packages';$legacyIndexContext=Context 'forge' '14.23.5.2855';$legacyIndexContext.MinecraftVersion='1.12.2'
 $legacyJar=New-Jar (Join-Path $legacyFolder 'manual.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='manual_legacy';version='1'})} ([IO.Compression.CompressionLevel]::NoCompression)
 $newLegacy=New-Jar (Join-Path $packages 'replacement.jar') @{'fixture/Mod.class'=(Legacy-Class @{modid='manual_legacy';version='2'})} ([IO.Compression.CompressionLevel]::NoCompression)
 $originalInfo=Get-Item -LiteralPath $legacyJar;$written=$originalInfo.LastWriteTimeUtc;$created=$originalInfo.CreationTimeUtc
 Assert ((Scan $legacyFolder $legacyIndexContext).Mods[0].Version -eq '1') 'Legacy manual-rebuild fixture was not indexed.'
 Assert ((Get-Item -LiteralPath $newLegacy).Length -eq $originalInfo.Length) 'Manual-rebuild fixture must preserve the file size.'
 [IO.File]::WriteAllBytes($legacyJar,[IO.File]::ReadAllBytes($newLegacy));[IO.File]::SetCreationTimeUtc($legacyJar,$created);[IO.File]::SetLastWriteTimeUtc($legacyJar,$written)
 $manual=$inspector.GetMethod('RebuildIndex',$flags).Invoke($null,@([string]$legacyFolder,$legacyIndexContext,[Threading.CancellationToken]::None))
 Assert ($manual.Mods[0].Version -eq '2') 'Manual rebuild did not bypass both disk-tree and legacy class-metadata caches.'

 $cachePath=$indexType.GetProperty('CacheDirectory',$flags).GetValue($null,$null);$blockedCache=Join-Path $fixture 'cache-is-a-file';[IO.File]::WriteAllText($blockedCache,'fixture')
 try{
  $indexType.GetProperty('CacheDirectory',$flags).SetValue($null,$blockedCache,$null);$memoryOnly=Scan $folder $newRuntime
  Assert (-not (Index-Flag $memoryOnly 'IndexPersisted') -and -not $memoryOnly.HasErrors) 'An unavailable disk cache blocked dependency checking or falsely reported persistence.'
  $lease=[IO.File]::Open($consumer,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
  try{$memoryOnly=Scan $folder $newRuntime;Assert ((Index-Flag $memoryOnly 'IndexHit') -and -not (Index-Flag $memoryOnly 'IndexPersisted')) 'Memory fallback reopened JARs or falsely claimed its index was on disk.'}finally{$lease.Dispose()}
 }finally{$indexType.GetProperty('CacheDirectory',$flags).SetValue($null,$cachePath,$null)}

 if($ReadOnlyLegacyModsFolder){
  $legacyContext=Context 'forge' '14.23.5.2855';$legacyContext.MinecraftVersion='1.12.2';$legacyContext.LegacyForgeVersionFolder='1.12.2'
  $timer=[Diagnostics.Stopwatch]::StartNew();$actual=Scan $ReadOnlyLegacyModsFolder $legacyContext;$timer.Stop()
  Write-Output ('Read-only legacy pack: '+$actual.Mods.Count+' mods; '+$actual.Issues.Count+' issues; '+$actual.Notices.Count+' notices; '+[Math]::Round($timer.Elapsed.TotalSeconds,2)+'s')
  $actual.Issues|ForEach-Object {Write-Output ($_.Kind.ToString()+' '+$_.Message)}
  $actual.Mods|Where-Object {$_.Id -match 'hammer|mixin|hurt|scalingwealth|examplemod'}|ForEach-Object {Write-Output ('MOD '+$_.Id+' '+$_.Version+' '+[IO.Path]::GetFileName($_.RootPath))}
  $actual.Notices|Select-Object -First 8
  $timer.Restart();$cachedActual=Scan $ReadOnlyLegacyModsFolder $legacyContext;$timer.Stop()
  Assert ($cachedActual.Issues.Count -eq $actual.Issues.Count -and $cachedActual.Mods.Count -eq $actual.Mods.Count) 'Cached legacy pack scan changed its dependency result.'
  Write-Output ('Cached read-only legacy scan: '+[Math]::Round($timer.Elapsed.TotalSeconds,2)+'s')
  $indexType.GetMethod('ClearMemory',$flags).Invoke($null,@())|Out-Null
  $timer.Restart();$diskActual=Scan $ReadOnlyLegacyModsFolder $legacyContext;$timer.Stop()
  Assert ((Index-Flag $diskActual 'IndexHit') -and $diskActual.Mods.Count -eq $actual.Mods.Count -and $diskActual.Issues.Count -eq $actual.Issues.Count) 'The full legacy pack was reindexed after dropping process memory.'
  Write-Output ('Disk dependency-tree reuse: '+[Math]::Round($timer.Elapsed.TotalSeconds,2)+'s')
 }
 Write-Output ("Mod dependency fixtures passed: "+$script:assertions+" assertions; version boundaries, Fabric/Quilt/Forge/NeoForge, legacy @Mod and template metadata, aliases, multi-mod and nested jars, disabled/duplicate IDs, reverse graph, batch/transitive impacts, conservative errors, cancellation and read-only bytes.")
} finally {
 $full=[IO.Path]::GetFullPath($fixture)
 $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclModDependencies-')){Remove-Item -LiteralPath $full -Recurse -Force}
}
