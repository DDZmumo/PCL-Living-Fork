param([string]$ExecutablePath,[string]$VbcPath)
# GUID fixtures and injected API responses only. No live HTTP, launcher startup, accounts, or real mods.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclModDependencyOnline-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$framework=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
$jsonPath=Join-Path $root 'Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem,System.Net.Http
[Reflection.Assembly]::LoadFrom($jsonPath)|Out-Null
$script:checks=0
function Assert([bool]$condition,[string]$message){$script:checks++;if(-not $condition){throw $message}}
function Invoke-Static($type,[string]$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return ,$type.GetMethod($name,$flags).Invoke($null,$values)
}
function Json($value){return ,([Newtonsoft.Json.Linq.JToken]::Parse((ConvertTo-Json -InputObject $value -Depth 25 -Compress)))}
function Folder([string]$name){$path=Join-Path $fixture $name;[IO.Directory]::CreateDirectory($path)|Out-Null;return $path}
function Jar([string]$folder,[string]$name,[string]$id=''){
 $path=Join-Path $folder $name;$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try {
  $entry=if($id){'fabric.mod.json'}else{'fixture.txt'}
  $text=if($id){@{schemaVersion=1;id=$id;version='1.0'}|ConvertTo-Json -Compress}else{'unrecognised fixture '+$name}
  $writer=[IO.StreamWriter]::new($zip.CreateEntry($entry).Open());try{$writer.Write($text)}finally{$writer.Dispose()}
 }finally{$zip.Dispose()}
 return $path
}
Add-Type -ReferencedAssemblies @($jsonPath,(Join-Path $framework 'System.Net.Http.dll'),(Join-Path $framework 'System.Core.dll')) -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
public sealed class ModDependencyOnlineFixtureNetwork {
 public readonly List<string> Calls=new List<string>();
 public readonly List<string> Bodies=new List<string>();
 public readonly List<string> Unexpected=new List<string>();
 public JToken Versions=new JObject(), Fingerprints=JObject.Parse("{\"data\":{\"exactMatches\":[]}}");
 public JToken Projects=new JArray(), CfProjects=JObject.Parse("{\"data\":[]}"), VersionProjects=new JArray();
 public JToken Search=JObject.Parse("{\"hits\":[]}"), CfSearch=JObject.Parse("{\"data\":[]}");
 public DateTime UtcNow=new DateTime(2026,10,25,12,0,0,DateTimeKind.Utc);
 public bool Fail, Delay;
 public Exception Failure;
 public CancellationTokenSource Cancel;
 public DateTime Clock(){return UtcNow;}
 public JToken Request(string url,HttpMethod method,string body,CancellationToken token){
  token.ThrowIfCancellationRequested();
  Calls.Add(url);Bodies.Add(body);
  if(Cancel!=null){Cancel.Cancel();token.ThrowIfCancellationRequested();}
  if(Delay){token.WaitHandle.WaitOne(2000);token.ThrowIfCancellationRequested();}
  if(Failure!=null)throw Failure;
  if(Fail)throw new IOException("Synthetic offline failure");
  JToken value=null;
  if(url=="https://api.modrinth.com/v2/version_files")value=Versions;
  else if(url.StartsWith("https://api.modrinth.com/v2/projects?"))value=Projects;
  else if(url.StartsWith("https://api.modrinth.com/v2/versions?"))value=VersionProjects;
  else if(url=="https://api.curseforge.com/v1/fingerprints/432")value=Fingerprints;
  else if(url=="https://api.curseforge.com/v1/mods")value=CfProjects;
  else if(url.StartsWith("https://api.modrinth.com/v2/search?"))value=Search;
  else if(url.StartsWith("https://api.curseforge.com/v1/mods/search?"))value=CfSearch;
  else {Unexpected.Add(url);throw new InvalidOperationException("Unexpected mocked API address");}
  return value.DeepClone();
 }
 public static uint Fingerprint(string path){
  byte[] bytes=File.ReadAllBytes(path).Where(b=>b!=9&&b!=10&&b!=13&&b!=32).ToArray();
  unchecked {
   uint h=1u^(uint)bytes.Length;int i=0;
   for(;i+4<=bytes.Length;i+=4){
    uint k=(uint)(bytes[i]|bytes[i+1]<<8|bytes[i+2]<<16|bytes[i+3]<<24);
    k*=0x5bd1e995;k^=k>>24;k*=0x5bd1e995;h*=0x5bd1e995;h^=k;
   }
   switch(bytes.Length-i){case 3:h^=(uint)bytes[i+2]<<16;goto case 2;case 2:h^=(uint)bytes[i+1]<<8;goto case 1;case 1:h^=bytes[i];h*=0x5bd1e995;break;}
   h^=h>>13;h*=0x5bd1e995;return h^(h>>15);
  }
 }
}
'@
try {
 if(-not $ExecutablePath){
  if(-not $VbcPath){$VbcPath='C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\vbc.exe'}
  if(-not [IO.File]::Exists($VbcPath)){throw 'Pass -VbcPath for Roslyn, or -ExecutablePath for a built launcher.'}
  $stub=Join-Path $fixture 'InstanceStub.vb'
  [IO.File]::WriteAllText($stub,@'
Imports System.Collections.Generic
Imports System.Net.Http
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
Friend Module DependencyFixtureNetworkBoundary
 Friend Sub SecretHeadersSign(Url As String, ByRef Request As HttpRequestMessage)
  Throw New InvalidOperationException("Live HTTP is disabled in this standalone fixture.")
 End Sub
End Module
'@)
  $ExecutablePath=Join-Path $fixture 'ModDependencyOnlineFixture.dll'
  $refs=@('System.dll','System.Core.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Net.Http.dll')|ForEach-Object {Join-Path $framework $_}
  & $VbcPath @('/nologo','/target:library','/rootnamespace:PCL','/optioninfer+','/optionstrict+','/utf8output',('/sdkpath:'+$framework),('/out:'+$ExecutablePath),('/reference:'+($refs -join ',')),('/reference:'+$jsonPath),(Join-Path $root 'Plain Craft Launcher 2/Modules/Minecraft/ModDependencyInspector.vb'),(Join-Path $root 'Plain Craft Launcher 2/Modules/Minecraft/ModDependencyOnline.vb'),$stub)
  if($LASTEXITCODE -ne 0){throw 'Isolated online dependency compilation failed.'}
 }else {
  foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll')){
   $dependency=Join-Path $root $path;if([IO.File]::Exists($dependency)){[Reflection.Assembly]::LoadFrom($dependency)|Out-Null}
  }
 }
 $asm=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ExecutablePath)))
 $inspector=$asm.GetType('PCL.ModDependencyInspector',$true)
 $online=$asm.GetType('PCL.ModDependencyOnline',$true)
 $contextType=$asm.GetType('PCL.ModDependencyInspector+InspectionContext',$true)
 $optionType=$asm.GetType('PCL.ModDependencyOnline+LookupOptions',$true)
 $cacheType=$asm.GetType('PCL.ModDependencyOnline+LookupCache',$true)
 $none=[Threading.CancellationToken]::None
 function Scan([string]$folder){
  $context=[Activator]::CreateInstance($contextType);$context.MinecraftVersion='1.20.1';$context.JavaVersion='17';$context.LoaderKnown=$true;$context.LoaderVersions.Add('fabricloader','0.16')
  return ,(Invoke-Static $inspector 'Inspect' @($folder,$context,$none))
 }
 function Options($network,$cache=$null){
  $value=[Activator]::CreateInstance($optionType)
  $value.Request=[Delegate]::CreateDelegate([Func[string,Net.Http.HttpMethod,string,Threading.CancellationToken,Newtonsoft.Json.Linq.JToken]],$network,$network.GetType().GetMethod('Request'))
  $value.Clock=[Delegate]::CreateDelegate([Func[datetime]],$network,$network.GetType().GetMethod('Clock'))
  $value.Cache=if($cache){$cache}else{[Activator]::CreateInstance($cacheType)}
  $value.MaxSearchFiles=0
  return $value
 }
 function Enrich($report,[string]$folder,$options,$token=$none){return ,(Invoke-Static $online 'Enrich' @($report,$folder,$token,$options))}
 function MrVersion([string]$path,[string]$project,[string]$version,[object[]]$deps=@()){
  return @{id=$version;project_id=$project;version_number='1.0';dependencies=$deps;files=@(@{hashes=@{sha1=(Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash.ToLowerInvariant()};size=([IO.FileInfo]$path).Length})}
 }
 function CfFile([string]$path,[int]$project,[int]$version,[object[]]$deps=@()){
  return @{id=$version;modId=$project;gameId=432;fileLength=([IO.FileInfo]$path).Length;fileFingerprint=[ModDependencyOnlineFixtureNetwork]::Fingerprint($path);displayName='CF version';dependencies=$deps;hashes=@(@{algo=1;value=(Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash.ToLowerInvariant()})}
 }
 function FileInfo($report,[string]$path){return $report.OnlineFiles|Where-Object {$_.FullPath -eq $path}|Select-Object -First 1}

 $folder=Folder 'exact';$owner=Jar $folder 'owner.jar';$peer=Jar $folder 'peer.jar' 'local_peer'
 $ownerHash=(Get-FileHash -LiteralPath $owner -Algorithm SHA1).Hash.ToLowerInvariant();$peerHash=(Get-FileHash -LiteralPath $peer -Algorithm SHA1).Hash.ToLowerInvariant()
 $network=[ModDependencyOnlineFixtureNetwork]::new()
 $data=@{};$data[$ownerHash]=MrVersion $owner 'OwnerP' 'OwnerV' @(@{project_id='PeerP';dependency_type='required'},@{project_id='OptP';dependency_type='optional'},@{version_id='OnlyVer';dependency_type='required'},@{file_name='peer.jar';dependency_type='required'})
 $data[$peerHash]=MrVersion $peer 'PeerP' 'PeerV' @(@{project_id='OwnerP';dependency_type='required'})
 $network.Versions=Json $data
 $network.VersionProjects=Json @(@{id='OnlyVer';project_id='OnlyP'})
 $network.Projects=Json @(@{id='OwnerP';title='Owner project';project_type='mod'},@{id='PeerP';title='Peer project';project_type='mod'},@{id='OptP';title='Optional project';project_type='mod'},@{id='OnlyP';title='Version-only project';project_type='mod'})
 $options=Options $network
 $report=Scan $folder;$localMods=$report.Mods.Count;$localIssues=$report.Issues.Count;$localErrors=$report.HasErrors
 $report=Enrich $report $folder $options
 $info=FileInfo $report $owner;$match=$info.Matches|Where-Object Platform -eq 'Modrinth'|Select-Object -First 1
 Assert ($report.OnlineAttempted -and $info.IsSupplementTarget -and $match.IsExact -and $match.ProjectName -eq 'Owner project') 'Modrinth exact hash match or title lookup failed.'
 Assert ($match.Dependencies.Count -eq 4 -and @($match.Dependencies|Where-Object Kind -eq 'optional').Count -eq 1) 'Required/optional platform declarations were not retained.'
 $required=$match.Dependencies|Where-Object ProjectId -eq 'PeerP'|Select-Object -First 1
 Assert ($required.EnabledPaths.Count -eq 1 -and $required.EnabledPaths[0] -eq $peer) 'A platform dependency was not mapped to an exact peer-file match.'
 Assert (@($match.Dependencies|Where-Object ProjectId -eq 'OnlyP').Count -eq 1) 'A version-only Modrinth dependency was not resolved to its platform project.'
 $unmapped=$match.Dependencies|Where-Object {$_.ProjectId -eq ''}|Select-Object -First 1
 Assert ($unmapped.MatchedPaths.Count -eq 0) 'A dependency filename must not map a platform project without a hash match.'
 Assert ($report.Mods.Count -eq $localMods -and $report.Issues.Count -eq $localIssues -and $report.HasErrors -eq $localErrors) 'Online platform declarations changed the local loader graph or errors.'
 Assert (@($report.Mods|Where-Object Id -eq 'OwnerP').Count -eq 0) 'A platform project ID leaked into loader Mod IDs.'
 Assert ($report.DescribeFile($owner).Contains('Modrinth') -and $report.DescribeFile($owner).Contains('Owner project')) 'Online source details were not included in file description.'
 Assert ($report.DescribeFile($peer).Contains('owner.jar')) 'Reverse platform relationship was not displayed.'
 Assert ($report.DescribeOnlineImpacts([string[]]@($peer)).Contains('owner.jar')) 'Platform-declared removal impact is missing.'
 Assert ((Invoke-Static $inspector 'DescribeImpacts' @($report,[string[]]@($peer))).Contains('下载源')) 'Existing removal text did not include the platform supplement.'
 Assert ($report.DescribeOnlineImpacts([string[]]@($owner,$peer)) -eq '') 'A selected owner must not be warned about its own batch.'
 Assert ($report.DescribeOnlineImpacts([string[]]@($owner)) -eq '') 'Reliable local declarations must take priority over contradictory platform-only removal requirements.'
 Assert (@($network.Unexpected).Count -eq 0) 'Unexpected mocked endpoint was called.'
 $requestCount=$network.Calls.Count;$report=Enrich (Scan $folder) $folder $options
 Assert ($network.Calls.Count -eq $requestCount -and $report.OnlineRequests -eq 0) 'Content-hash cache did not reuse exact and negative results.'
 [IO.File]::Move($peer,$peer+'.disabled')
 $disabledReport=Enrich (Scan $folder) $folder $options
 $disabledRequirement=((FileInfo $disabledReport $owner).Matches|Where-Object Platform -eq 'Modrinth').Dependencies|Where-Object ProjectId -eq 'PeerP'|Select-Object -First 1
 Assert ($disabledRequirement.MatchedPaths.Count -eq 1 -and $disabledRequirement.EnabledPaths.Count -eq 0 -and $disabledRequirement.Status.Contains('禁用')) 'A cached content match retained a stale path or enabled state.'
 Assert ($disabledReport.DescribeOnlineImpacts([string[]]@($peer+'.disabled')) -eq '') 'Deleting a disabled online provider should not warn about losing an enabled dependency.'
 [IO.File]::Move($peer+'.disabled',$peer)
 $network.UtcNow=$network.UtcNow.AddHours(7);$report=Enrich (Scan $folder) $folder $options
 Assert ($network.Calls.Count -gt $requestCount) 'Expired content-hash cache did not refresh.'
 $oldHash=$ownerHash;[IO.File]::AppendAllText($owner,'fixture changed bytes')
 $requestCount=$network.Calls.Count;$report=Enrich (Scan $folder) $folder $options
 Assert ((FileInfo $report $owner).Sha1 -ne $oldHash -and $network.Calls.Count -gt $requestCount) 'Changed content reused stale hash results.'
 Assert ((FileInfo $report $owner).Matches.Count -eq 0 -and -not $report.HasErrors) 'A changed file retained stale platform dependency assertions.'

 $folder=Folder 'search';$unknown=Jar $folder 'Example-Mod-fabric-1.20.1.jar';$network=[ModDependencyOnlineFixtureNetwork]::new()
 $network.Search=Json @{hits=@(@{project_id='Candidate1';title='First candidate';project_type='mod'},@{project_id='Pack';title='Not a mod';project_type='modpack'})}
 $network.CfSearch=Json @{data=@(@{id=75;name='CF candidate';classId=6;links=@{websiteUrl='https://evil.invalid/key-trap'}})}
 $options=Options $network;$options.MaxSearchFiles=1
 $report=Enrich (Scan $folder) $folder $options;$info=FileInfo $report $unknown
 Assert ($info.Matches.Count -eq 0 -and $info.Candidates.Count -eq 2 -and -not @($info.Candidates|Where-Object IsExact).Count) 'Filename search auto-selected a candidate or included a non-mod project.'
 Assert (-not $report.HasErrors -and $report.Issues.Count -eq 0) 'Search candidates produced a definite missing/version error.'
 Assert ($info.Candidates[1].Website.StartsWith('https://www.curseforge.com/')) 'An untrusted candidate website was retained.'
 Assert ($report.DescribeFile($unknown).Contains('未选定') -and $report.DescribeOnlineImpacts([string[]]@($unknown)) -eq '') 'Candidate-only lookup did not remain informational.'
 $query=Invoke-Static $online 'SearchQuery' @('Example-Mod-fabric-1.20.1.jar.disabled')
 Assert ($query -eq 'Example Mod') 'Filename search query retained loader/version/disabled suffix.'
 Assert ($network.Calls.Count -le 12 -and @($network.Unexpected).Count -eq 0) 'Candidate lookup exceeded its endpoint/request boundaries.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$candidateHash=(Get-FileHash -LiteralPath $unknown -Algorithm SHA1).Hash.ToLowerInvariant()
 $forged=MrVersion $unknown 'ForgedP' 'ForgedV';$forged.files[0].hashes.sha1='a'*40;$values=@{};$values[$candidateHash]=$forged;$network.Versions=Json $values
 $report=Enrich (Scan $folder) $folder (Options $network)
 Assert ((FileInfo $report $unknown).Matches.Count -eq 0) 'A Modrinth hash-map key bypassed file-hash validation.'
 $forged=MrVersion $unknown 'PackP' 'PackV';$values=@{};$values[$candidateHash]=$forged;$network.Versions=Json $values;$network.Projects=Json @(@{id='PackP';title='Pack';project_type='modpack'})
 $report=Enrich (Scan $folder) $folder (Options $network)
 Assert ((FileInfo $report $unknown).Matches.Count -eq 0) 'A non-mod Modrinth project was retained as a mod dependency source.'

 $folder=Folder 'curseforge';$owner=Jar $folder 'cf-owner.jar';$peer=Jar $folder 'cf-peer.jar' 'local_cf_peer';$network=[ModDependencyOnlineFixtureNetwork]::new()
 $cfOwner=CfFile $owner 42 101 @(@{modId=43;relationType=3},@{modId=44;relationType=2})
 $cfPeer=CfFile $peer 43 102
 $network.Fingerprints=Json @{data=@{exactMatches=@(@{id=42;file=$cfOwner},@{id=43;file=$cfPeer})}}
 $network.CfProjects=Json @{data=@(@{id=42;name='CF owner';classId=6;links=@{websiteUrl='https://www.curseforge.com/minecraft/mc-mods/fixture-owner'}},@{id=43;name='CF peer';classId=6},@{id=44;name='CF optional';classId=6})}
 $options=Options $network;$report=Enrich (Scan $folder) $folder $options;$info=FileInfo $report $owner;$match=$info.Matches|Where-Object Platform -eq 'CurseForge'|Select-Object -First 1
 Assert ($match.IsExact -and $match.ProjectName -eq 'CF owner' -and $match.MatchBasis.Contains('内容哈希')) 'CurseForge exact fingerprint plus hash match failed.'
 Assert (@($match.Dependencies|Where-Object {$_.Kind -eq 'required' -and $_.EnabledPaths.Count -eq 1}).Count -eq 1 -and @($match.Dependencies|Where-Object Kind -eq 'optional').Count -eq 1) 'CurseForge relationType required/optional or peer mapping failed.'
 Assert ($report.DescribeFile($peer).Contains('cf-owner.jar') -and -not $report.HasErrors) 'CurseForge reverse relationship or conservative local result failed.'
 $fingerprintBody=$network.Bodies[$network.Calls.IndexOf('https://api.curseforge.com/v1/fingerprints/432')]
 Assert ($fingerprintBody.Contains([string][ModDependencyOnlineFixtureNetwork]::Fingerprint($owner))) 'Streamed CurseForge fingerprint differed from the reference filter/seed implementation.'
 $cfOwner.hashes=@(@{algo=1;value=('a'*40)});$network.Fingerprints=Json @{data=@{exactMatches=@(@{id=42;file=$cfOwner})}}
 $report=Enrich (Scan $folder) $folder (Options $network)
 Assert ((FileInfo $report $owner).Matches.Count -eq 0) 'A CurseForge fingerprint collision bypassed the content-hash check.'
 $cfOwner.hashes=@(@{algo=2;value=(Get-FileHash -LiteralPath $owner -Algorithm MD5).Hash.ToLowerInvariant()});$network.Fingerprints=Json @{data=@{exactMatches=@(@{id=42;file=$cfOwner})}}
 $report=Enrich (Scan $folder) $folder (Options $network)
 Assert ((FileInfo $report $owner).Matches.Count -eq 1) 'CurseForge exact-match MD5 fallback was not accepted.'
 $cfOwner.gameId=999;$network.Fingerprints=Json @{data=@{exactMatches=@(@{id=42;file=$cfOwner})}}
 Assert ((FileInfo (Enrich (Scan $folder) $folder (Options $network)) $owner).Matches.Count -eq 0) 'A non-Minecraft CurseForge match was accepted.'
 $cfOwner.gameId=432;$network.Fingerprints=Json @{data=@{exactMatches=@(@{id=42;file=$cfOwner})}};$network.CfProjects=Json @{data=@(@{id=42;name='Not a mod';classId=12;links=@{websiteUrl='https://www.curseforge.com/minecraft/mc-mods/misleading-path'}})}
 Assert ((FileInfo (Enrich (Scan $folder) $folder (Options $network)) $owner).Matches.Count -eq 0) 'An explicit non-mod CurseForge class ID was overridden by its website path.'

 $folder=Folder 'failure';$unknown=Jar $folder 'offline.jar';$null=Jar $folder 'duplicate-one.jar' 'duplicate';$null=Jar $folder 'duplicate-two.jar' 'duplicate'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$network.Fail=$true;$options=Options $network;$local=Scan $folder;$beforeErrors=$local.Issues.Count
 $report=Enrich $local $folder $options
 Assert ($report.HasErrors -and $report.Issues.Count -eq $beforeErrors -and $report.OnlineNotes.Count -gt 0) 'Offline failure changed existing definite local errors.'
 $calls=$network.Calls.Count;$report=Enrich (Scan $folder) $folder $options
 Assert ($network.Calls.Count -eq $calls) 'Network failure cooldown repeatedly requested the same platform.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$httpType=$asm.GetType('PCL.ModDependencyOnline+HttpFailure',$true)
 $network.Failure=[Activator]::CreateInstance($httpType,$flags,$null,@(429,120),$null)
 $options=Options $network;$report=Enrich (Scan $folder) $folder $options
 Assert (($report.OnlineNotes -join ' ').Contains('过于频繁')) '429 response was not handled as a bounded cooldown.'
 $calls=$network.Calls.Count;$network.UtcNow=$network.UtcNow.AddSeconds(60);$report=Enrich (Scan $folder) $folder $options
 Assert ($network.Calls.Count -eq $calls) 'Retry-After cooldown was not respected.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 try{$report=Enrich (Scan $folder) $folder (Options $network) $cancel.Token}finally{$cancel.Dispose()}
 Assert ($report.OnlineCancelled -and $network.Calls.Count -eq 0 -and $report.Issues.Count -eq $beforeErrors) 'Pre-cancelled enrichment changed local results or made requests.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$cancel=[Threading.CancellationTokenSource]::new();$network.Cancel=$cancel
 try{$report=Enrich (Scan $folder) $folder (Options $network) $cancel.Token}finally{$cancel.Dispose()}
 Assert ($report.OnlineCancelled -and $network.Calls.Count -eq 1 -and $report.HasErrors) 'Mid-request cancellation did not retain local results and stop requests.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$network.Delay=$true;$options=Options $network;$options.TimeBudgetMs=150;$options.RequestTimeoutMs=1000;$watch=[Diagnostics.Stopwatch]::StartNew()
 $report=Enrich (Scan $folder) $folder $options;$watch.Stop()
 Assert ($report.OnlineTimedOut -and $watch.ElapsedMilliseconds -lt 1500 -and $report.HasErrors) 'Total network budget failed to bound a stalled request.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$options=Options $network;$options.MaxRequests=1;$report=Enrich (Scan $folder) $folder $options
 Assert ($report.OnlineRequests -eq 1 -and ($report.OnlineNotes -join ' ').Contains('数量上限')) 'Request count cap failed.'

 $folder=Folder 'complete';$path=Jar $folder 'complete.jar' 'complete';$network=[ModDependencyOnlineFixtureNetwork]::new();$local=Scan $folder;$report=Enrich $local $folder (Options $network)
 Assert (-not $report.OnlineAttempted -and $network.Calls.Count -eq 0) 'Complete reliable local metadata should not trigger online lookup.'
 $disabled=Jar $folder 'unknown.jar.disabled';$local=Scan $folder
 Assert (-not $local.MetadataIncomplete) 'An unread disabled mod should not make startup metadata incomplete.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$report=Enrich $local $folder (Options $network)
 Assert (-not $report.OnlineAttempted -and $network.Calls.Count -eq 0) 'Startup enrichment was triggered by an unread disabled mod.'
 $disabledHash=(Get-FileHash -LiteralPath $disabled -Algorithm SHA1).Hash.ToLowerInvariant();$knownHash=(Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash.ToLowerInvariant()
 $values=@{};$values[$disabledHash]=MrVersion $disabled 'DisabledP' 'DisabledV' @(@{project_id='CompleteP';dependency_type='required'});$values[$knownHash]=MrVersion $path 'CompleteP' 'CompleteV'
 $network.Versions=Json $values;$network.Projects=Json @(@{id='DisabledP';title='Disabled project';project_type='mod'},@{id='CompleteP';title='Complete project';project_type='mod'})
 $options=Options $network;$options.TargetFile=$disabled;$report=Enrich (Scan $folder) $folder $options;$info=FileInfo $report $disabled
 Assert ($report.OnlineAttempted -and $network.Calls.Count -gt 0 -and $info.IsSupplementTarget -and -not $info.Enabled -and $info.Matches.Count -eq 1) 'Explicit disabled-file details did not trigger exact online supplementation.'
 Assert ($info.Matches[0].Dependencies[0].EnabledPaths.Count -eq 1 -and $report.DescribeFile($disabled).Contains('Disabled project')) 'Disabled-file detail lost its online dependency or caption.'
 Assert (-not $report.MetadataIncomplete -and -not $report.HasErrors -and $report.Issues.Count -eq 0) 'Explicit disabled-file lookup changed startup metadata or local errors.'
 $combined=Invoke-Static $online 'InspectWithOnline' @($folder,$report.Context,$none,$options)
 Assert ($combined.OnlineAttempted -and (FileInfo $combined $disabled).Matches.Count -eq 1) 'InspectWithOnline did not forward the explicit detail target.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$options=Options $network;$options.TargetFile=$path;$report=Enrich (Scan $folder) $folder $options
 Assert (-not $report.OnlineAttempted -and $network.Calls.Count -eq 0) 'A reliable explicit detail target caused unnecessary lookup for another disabled file.'
 $outside=Jar (Folder 'outside-target') 'outside.jar.disabled';$options=Options $network;$options.TargetFile=$outside;$report=Enrich (Scan $folder) $folder $options
 Assert (-not $report.OnlineAttempted -and $network.Calls.Count -eq 0) 'Explicit lookup accepted a file outside the mod folder.'
 $network=[ModDependencyOnlineFixtureNetwork]::new();$network.Search=Json @{hits=@(@{project_id='DisabledCandidate';title='Disabled search candidate';project_type='mod'})}
 $options=Options $network;$options.TargetFile=$disabled;$options.MaxSearchFiles=1;$report=Enrich (Scan $folder) $folder $options
 Assert ((FileInfo $report $disabled).Matches.Count -eq 0 -and (FileInfo $report $disabled).Candidates.Count -eq 1 -and -not $report.HasErrors) 'An unmatched explicit disabled target did not remain a candidate-only search.'
 $mixed=Folder 'mixed-disabled';$null=Jar $mixed 'active-unknown.jar';$mixedDisabled=Jar $mixed 'disabled-unknown.jar.disabled';$network=[ModDependencyOnlineFixtureNetwork]::new()
 $report=Enrich (Scan $mixed) $mixed (Options $network)
 Assert ($report.OnlineAttempted -and -not (FileInfo $report $mixedDisabled).IsSupplementTarget) 'Startup treated an unread disabled peer as a lookup/search target.'
 foreach($url in @('https://api.modrinth.com/v2/version_files','https://api.curseforge.com/v1/fingerprints/432')){Assert ([bool](Invoke-Static $online 'IsAllowedApi' @($url))) 'Official dependency API was rejected.'}
 foreach($url in @('http://api.curseforge.com/v1/mods','https://evil.invalid/v1/mods','https://api.curseforge.com.evil.invalid/v1/mods','https://api.curseforge.com:444/v1/mods','https://user@api.curseforge.com/v1/mods','https://cdn.modrinth.com/v2/version_files')){Assert (-not [bool](Invoke-Static $online 'IsAllowedApi' @($url))) 'Credential boundary accepted a foreign host, port, protocol or userinfo.'}
 Write-Output ("Online dependency fixtures passed: "+$script:checks+" assertions; exact SHA1/fingerprint+hash matches, both platforms, required/optional and reverse links, conservative candidates, unchanged local errors, content cache/expiry, failure/429/cancellation/time/request caps and API host boundaries. No live HTTP.")
} finally {
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclModDependencyOnline-[a-f0-9]{32}\z'){throw 'Unsafe online dependency fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
