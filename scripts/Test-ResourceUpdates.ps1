param([Parameter(Mandatory=$true)][string]$ExecutablePath)
# Synthetic API responses and temporary ZIPs only; no real network, accounts or games.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.IO.Compression,System.IO.Compression.FileSystem,System.Net.Http
foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
public static class ResourceUpdateTestLogger {
 public static readonly List<string> Errors=new List<string>();
 public static void Start() {AppDomain.CurrentDomain.FirstChanceException += Capture;}
 private static void Capture(object sender,FirstChanceExceptionEventArgs args) {
  string stack=args.Exception.StackTrace;
  if(stack!=null&&stack.Contains("InstanceResourceUpdates"))Errors.Add(args.Exception.ToString());
 }
}
'@
[ResourceUpdateTestLogger]::Start()
$asm=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$flags=[Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$settings=$asm.GetType('PCL.Settings').GetField('Entries').GetValue($null)
foreach($entry in $settings.Values){$entry.ValueCache['']=$entry.DefaultValue}
$settings['Identify'].ValueCache['']='synthetic-resource-updates'
$store=$asm.GetType('PCL.InstanceResourceStore');$updates=$asm.GetType('PCL.InstanceResourceUpdates');$kindType=$asm.GetType('PCL.InstanceResourceKind')
$packKind=[Enum]::Parse($kindType,'ResourcePacks');$shaderKind=[Enum]::Parse($kindType,'ShaderPacks')
$none=[Threading.CancellationToken]::None
function Invoke-Static($type,$name,[object[]]$arguments){
 $values=New-Object object[] $arguments.Length
 for($i=0;$i -lt $values.Length;$i++){if($null -ne $arguments[$i]){$values[$i]=$arguments[$i].PSObject.BaseObject}}
 return ,$type.GetMethod($name,$flags).Invoke($null,$values)
}
function Assert-Rejected([scriptblock]$work,[string]$message){$rejected=$false;try{& $work|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $message}}
function Zip([string]$path,[string]$content,[bool]$shader=$false){
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
 $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try{$writer=[IO.StreamWriter]::new($archive.CreateEntry($(if($shader){'shaders/test.fsh'}else{'pack.mcmeta'})).Open());try{$writer.Write($content)}finally{$writer.Dispose()}}finally{$archive.Dispose()}
}
function Check($kind=$packKind){return ,(Invoke-Static $updates 'Check' @($packs,$items,$kind,'1.20.1',$none,$request,$fingerprint))}
function Install($result,$downloader=$download){Invoke-Static $updates 'Install' @($packs,$result,$recycle,$packKind,'1.20.1',$none,$downloader)|Out-Null}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclResourceUpdates-'+[Guid]::NewGuid().ToString('N'))
try{
 $packs=Join-Path $fixture 'resourcepacks';$original=Join-Path $packs 'My selected pack.zip';$newFile=Join-Path $fixture 'download/new.zip'
 Zip $original '{"pack":{"pack_format":15,"description":"Original"}}'
 Zip $newFile '{"pack":{"pack_format":15,"description":"Updated"}}'
 $hash=(Get-FileHash -LiteralPath $original -Algorithm SHA1).Hash.ToLowerInvariant();$newHash=(Get-FileHash -LiteralPath $newFile -Algorithm SHA1).Hash.ToLowerInvariant()
 $oldBytes=[IO.File]::ReadAllBytes($original);$oldSize=$oldBytes.Length;$newSize=(Get-Item -LiteralPath $newFile).Length
 $items=Invoke-Static $store 'Scan' @($packs,$packs,$packKind,$none)
 $recycle=Join-Path $fixture 'PCL/ResourceRecycle/ResourcePacks'
 $old=@{id='old';project_id='project';name='Version 1';version_number='1.0';date_published='2025-01-01T00:00:00Z';version_type='release';downloads=1;game_versions=@('1.20.1');loaders=@('minecraft');dependencies=@();files=@(@{filename='Old.zip';url='https://cdn.modrinth.com/data/project/Old.zip';size=$oldSize;primary=$true;hashes=@{sha1=$hash}})}
 $candidate=$old.Clone();$candidate.id='new';$candidate.name='Version 2';$candidate.version_number='2.0';$candidate.date_published='2025-02-01T00:00:00Z';$candidate.files=@(@{filename='New.zip';url='https://cdn.modrinth.com/data/project/New.zip';size=$newSize;primary=$true;hashes=@{sha1=$newHash}})
 $cfOld=@{id=1234567;gameId=432;modId=42;displayName='CF 1';fileName='Old.zip';fileLength=$oldSize;fileDate='2025-01-01T00:00:00Z';releaseType=1;downloadCount=1;gameVersions=@('1.20.1');downloadUrl='https://edge.forgecdn.net/files/1234/567/Old.zip';fileFingerprint=7;hashes=@(@{algo=1;value=$hash});dependencies=@();isAvailable=$true}
 $cfNew=$cfOld.Clone();$cfNew.id=1234568;$cfNew.displayName='CF 2';$cfNew.fileName='New.zip';$cfNew.fileLength=$newSize;$cfNew.fileDate='2025-02-01T00:00:00Z';$cfNew.downloadUrl='https://edge.forgecdn.net/files/1234/568/New.zip';$cfNew.hashes=@(@{algo=1;value=$newHash})
 $mode='modrinth';$projectType='resourcepack';$calls=[Collections.Generic.List[string]]::new()
 $fingerprint=[Func[string,uint32]]{param($path) return [uint32]7}
 $request=[Func[string,Net.Http.HttpMethod,string,Newtonsoft.Json.Linq.JToken]]{
  param($url,$method,$body)
  $calls.Add($url)
  $value=$null
  if($url -eq 'https://api.modrinth.com/v2/version_files'){
   $value=@{};if($mode -eq 'modrinth'){$value[$hash]=$old}
  }elseif($url.StartsWith('https://api.modrinth.com/v2/projects?')){
   $value=@(@{id='project';title='Matched pack';project_type=$projectType})
  }elseif($url -eq 'https://api.modrinth.com/v2/version_files/update_individual'){
   if(-not $body.Contains('1.20.1') -or -not $body.Contains($(if($projectType -eq 'shader'){'iris'}else{'minecraft'})) -or -not $body.Contains('version_types')){throw 'Missing exact compatibility/channel filters'}
   $value=@{};$value[$hash]=$candidate
  }elseif($url -eq 'https://api.curseforge.com/v1/fingerprints/432'){
   if($mode -eq 'failure'){throw 'Synthetic API outage'}
   $value=@{data=@{exactMatches=@()}}
   if($mode -eq 'curseforge'){$value.data.exactMatches=@(@{id=42;file=$cfOld})}
  }elseif($url -eq 'https://api.curseforge.com/v1/mods'){
   $value=@{data=@(@{id=42;name='CF pack';isAvailable=$true;allowModDistribution=$true;links=@{websiteUrl='https://www.curseforge.com/minecraft/texture-packs/fixture'}})}
  }elseif($url.StartsWith('https://api.curseforge.com/v1/mods/42/files?')){
   if(-not $url.Contains('gameVersion=1.20.1')){throw 'Missing exact CurseForge game version filter'}
   $value=@{data=@($cfNew);pagination=@{totalCount=1}}
  }else{throw 'Unexpected API request'}
  return ,([Newtonsoft.Json.Linq.JToken]::Parse((ConvertTo-Json -InputObject $value -Depth 15 -Compress)))
 }
 $result=Check
 if($result.Count -ne 1 -or -not $result[0].HasUpdate -or $result[0].UpdateVersion.Hash -cne $newHash){
  throw ('Modrinth hash match or update selection failed: '+$result[0].Status+'; '+([ResourceUpdateTestLogger]::Errors -join ';'))
 }
 foreach($field in @('game_versions','loaders','project_id','version_type','date_published')){
  $saved=$candidate[$field]
  $candidate[$field]=switch($field){'game_versions'{,@('1.21.1')};'loaders'{,@('fabric')};'project_id'{'foreign'};'version_type'{'alpha'};'date_published'{'2024-01-01T00:00:00Z'}}
  if((Check)[0].HasUpdate){throw 'Incompatible, foreign, prerelease or older update was accepted'}
  $candidate[$field]=$saved
 }
 $projectType='mod';if((Check)[0].HasUpdate){throw 'A mod was matched as a resource pack'};$projectType='resourcepack'
 $old.files[0].primary=$false;$old.files+=@{filename='Primary.zip';primary=$true;hashes=@{sha1=('a'*40)}}
 if((Check)[0].HasUpdate){throw 'A non-primary file was switched to another variant'}
 $old.files=@($old.files[0]);$old.files[0].primary=$true
 $mode='curseforge';$result=Check
 if(-not $result[0].HasUpdate -or $result[0].UpdateVersion.Hash -cne $newHash){throw 'CurseForge exact fingerprint/hash update failed'}
 $savedHash=$cfOld.hashes;$cfOld.hashes=@(@{algo=1;value=('a'*40)})
 if((Check)[0].HasUpdate){throw 'CurseForge fingerprint collision bypassed content hash validation'};$cfOld.hashes=$savedHash
 $savedUrl=$cfNew.downloadUrl;$cfNew.downloadUrl=$null
 if((Check)[0].HasUpdate){throw 'Blocked CurseForge download was reconstructed'};$cfNew.downloadUrl=$savedUrl
 $savedGames=$cfNew.gameVersions;$cfNew.gameVersions=@('1.21.1')
 if((Check)[0].HasUpdate){throw 'Wrong Minecraft version accepted from CurseForge'};$cfNew.gameVersions=$savedGames
 $mode='failure';if((Check)[0].Status -notmatch 'CurseForge'){throw 'API outage was presented as a complete check'}
 $mode='modrinth';$projectType='shader';$old.loaders=@('iris','optifine');$candidate.loaders=@('iris','optifine')
 $shaderResult=Check $shaderKind
 if(-not $shaderResult[0].HasUpdate){throw 'Shader compatibility did not retain its loader set'}
 $projectType='resourcepack';$old.loaders=@('minecraft');$candidate.loaders=@('minecraft');$result=Check
 $downloadType=[Action``2].MakeGenericType([Collections.Generic.List``1].MakeGenericType($asm.GetType('PCL.ModNet+NetFile')),[Threading.CancellationToken])
 $download=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($files,$token) foreach($file in $files){$token.ThrowIfCancellationRequested();[IO.File]::Copy($newFile,$file.LocalPath)}},$downloadType)
 if([string](Invoke-Static $updates 'FileHash' @($newFile,$false)) -cne $newHash){throw 'Built SHA1 helper differs from the ZIP checksum'}
 if($result[0].UpdateVersion.Hash -cne $newHash -or $result[0].UpdateVersion.Size -ne $newSize){throw 'Selected update hash/size changed before download'}
 Install $result $download
 if((Get-FileHash -LiteralPath $original -Algorithm SHA1).Hash.ToLowerInvariant() -cne $newHash){throw 'Resource update did not replace file contents'}
 if([IO.File]::Exists((Join-Path $packs 'New.zip'))){throw 'Update broke the selected resource-pack filename'}
 $backup=@([IO.Directory]::EnumerateFiles($recycle,'content',[IO.SearchOption]::AllDirectories))
 if($backup.Count -ne 1 -or (Get-FileHash -LiteralPath $backup[0] -Algorithm SHA1).Hash.ToLowerInvariant() -cne $hash){throw 'Original resource was not preserved for restore'}
 [IO.File]::WriteAllBytes($original,$oldBytes)
 $items=Invoke-Static $store 'Scan' @($packs,$packs,$packKind,$none);$result=Check
 $badDownload=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($files,$token)foreach($file in $files){[IO.File]::WriteAllText($file.LocalPath,'corrupt-download')}},$downloadType)
 Assert-Rejected {Install $result $badDownload} 'Corrupt download was accepted'
 if((Get-FileHash -LiteralPath $original -Algorithm SHA1).Hash.ToLowerInvariant() -cne $hash){throw 'Failed update changed the original file'}
 $failedDownload=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($files,$token)throw 'Synthetic download failure'},$downloadType)
 Assert-Rejected {Install $result $failedDownload} 'Download failure was not propagated'
 $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel()
 Assert-Rejected {Invoke-Static $updates 'Install' @($packs,$result,$recycle,$packKind,'1.20.1',$cancel.Token,$download)} 'Cancelled update was installed';$cancel.Dispose()
 [IO.File]::AppendAllText($original,'changed-after-check')
 Assert-Rejected {Install $result $download} 'Changed local file was replaced through a stale match'
 if(@([IO.Directory]::EnumerateDirectories($packs,'.pcl-import-*')).Count -ne 0){throw 'Update staging files remained'}
 Write-Output 'Resource updates passed: Modrinth/CurseForge exact matches, Minecraft/type/loader/channel/date/variant guards, collision and blocked-download checks, outage visibility, validated replacement with preserved filename/backup, cancellation, stale file and failed-download preservation. No network or real games used.'
}finally{
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclResourceUpdates-[a-f0-9]{32}\z'){throw 'Unsafe resource update fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
