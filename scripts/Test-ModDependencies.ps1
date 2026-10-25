param([string]$ExecutablePath,[string]$VbcPath)
# Read-only metadata fixtures in a GUID temp directory. No launcher entry point, game, real profile, or network.
# With no EXE, compile ONLY ModDependencyInspector.vb and a tiny instance adapter stub.
$ErrorActionPreference='Stop'
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
function New-Jar([string]$path,[hashtable]$entries){
 $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try {
  foreach($name in $entries.Keys){
   $stream=$archive.CreateEntry($name).Open()
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
try {
 if(-not $ExecutablePath){
  if(-not $VbcPath){
   $candidate=Get-Command vbc.exe -ErrorAction SilentlyContinue
   if($candidate){$VbcPath=$candidate.Source}
   else{$VbcPath='C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\vbc.exe'}
  }
  if(-not [IO.File]::Exists($VbcPath)){throw 'Pass -VbcPath for the VS Roslyn VB compiler, or -ExecutablePath for an already built launcher.'}
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
  $arguments=@('/nologo','/target:library','/rootnamespace:PCL','/optioninfer+','/optionstrict+','/utf8output',('/sdkpath:'+$framework),('/out:'+$ExecutablePath),('/reference:'+($references -join ',')),('/reference:'+$jsonPath),(Join-Path $root 'Plain Craft Launcher 2/Modules/Minecraft/ModDependencyInspector.vb'),$stub)
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
 $contextType=$asm.GetType('PCL.ModDependencyInspector+InspectionContext',$true)
 $inspect=$inspector.GetMethod('Inspect',$flags);$match=$inspector.GetMethod('MatchVersion',$flags)
 $getImpacts=$inspector.GetMethod('GetImpacts',$flags);$describeImpacts=$inspector.GetMethod('DescribeImpacts',$flags)
 function Context([string]$loader='fabricloader',[string]$version='0.16.0'){
  $context=[Activator]::CreateInstance($contextType)
  $context.MinecraftVersion='1.20.1';$context.JavaVersion='17.0.9';$context.LoaderKnown=$true
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
  @('0.2.9','^0.2.1','Satisfied'),@('0.3','^0.2.1','Unsatisfied'),@('1.0','1.0.0','Satisfied'),
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
 $null=Fabric $folder 'optional.jar' 'optional_library' '1.0'
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
 $null=Fabric $folder 'bad.jar' 'bad' '1.5'
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

 $folder=Folder 'legacy'
 $null=New-Jar (Join-Path $folder 'legacy.jar') @{'mcmod.info'='[{"modid":"legacy","name":"Legacy","version":"1.0","requiredMods":["old_library@[1,2)"]}]'}
 $report=Scan $folder (Context 'forge' '14.23')
 Assert ($report.Mods.Count -eq 1 -and $report.MetadataIncomplete -and -not $report.HasErrors) 'Legacy Forge declarations must remain conservative about annotation-only dependencies.'
 $versionFolder=Join-Path $folder '1.12.2';[IO.Directory]::CreateDirectory($versionFolder)|Out-Null
 $null=Fabric $versionFolder 'legacy-folder.jar' 'version_folder'
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
  $instance.Version.Reliable=$false
  $factory=$inspector.GetMethod('ContextForInstance',$flags).Invoke($null,@($instance,$null))
  Assert (-not $factory.MinecraftVersion) 'A guessed Minecraft version must not produce definite version mismatches.'
 }
 Write-Output ("Mod dependency fixtures passed: "+$script:assertions+" assertions; version boundaries, Fabric/Quilt/Forge/NeoForge, aliases, multi-mod and nested jars, disabled/duplicate IDs, reverse graph, batch/transitive impacts, conservative errors, cancellation and read-only bytes.")
} finally {
 $full=[IO.Path]::GetFullPath($fixture)
 $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if($full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('PclModDependencies-')){Remove-Item -LiteralPath $full -Recurse -Force}
}
