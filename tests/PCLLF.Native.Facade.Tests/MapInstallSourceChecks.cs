using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
namespace PCLLF.Native.Facade.Tests;
internal static class MapInstallSourceChecks
{
    private static readonly Dictionary<string,string> Original = new(StringComparer.Ordinal)
    {
        ["map:32"]="Test-MapInstallAndRecycle.ps1:32  if($archives -ne (Join-Path $saves 'PCLLFWorldDownload') -or -not [IO.Directory]::Exists($archives)){throw 'Map archive directory was not created under saves'}",
        ["map:33"]="Test-MapInstallAndRecycle.ps1:33  if((Call $installer 'GetDownloadFolder' @($saves)) -ne $archives){throw 'Existing archive directory was not reused'}",
        ["map:36"]="Test-MapInstallAndRecycle.ps1:36  if($world -ne (Join-Path $saves 'Flat map') -or [IO.Directory]::Exists((Join-Path $archives 'Flat map'))){throw 'Map was extracted inside the ZIP download directory'}",
        ["map:37"]="Test-MapInstallAndRecycle.ps1:37  Text (Join-Path $world 'level.dat') 'flat-world';Text (Join-Path $world 'region/data.txt') 'region-data'",
        ["map:38"]="Test-MapInstallAndRecycle.ps1:38  if((Get-FileHash -LiteralPath $flat).Hash -cne $hash){throw 'Auto install changed the original downloaded ZIP'}",
        ["map:40"]="Test-MapInstallAndRecycle.ps1:40  if($world -ceq $second){throw 'Repeat map download reused an existing world'};Text (Join-Path $world 'level.dat') 'flat-world'",
        ["map:44"]="Test-MapInstallAndRecycle.ps1:44  Text (Join-Path $saves 'Wrapped/level.dat') 'wrapped-world'",
        ["map:45"]="Test-MapInstallAndRecycle.ps1:45  $bad=Join-Path $archives 'Bad.zip';Zip $bad @{'../outside.txt'='bad';'level.dat'='bad'};Reject {Call $installer 'Install' @($bad,$saves,$none)} 'Map traversal accepted'",
        ["map:46"]="Test-MapInstallAndRecycle.ps1:46  if(Test-Path -LiteralPath (Join-Path $fixture 'outside.txt')){throw 'Map wrote outside staging'}",
        ["map:47"]="Test-MapInstallAndRecycle.ps1:47  $multiple=Join-Path $archives 'Multiple.zip';Zip $multiple @{'one/level.dat'='one';'two/level.dat'='two'};Reject {Call $installer 'Install' @($multiple,$saves,$none)} 'Multi-world archive silently selected a world'",
        ["map:48"]="Test-MapInstallAndRecycle.ps1:48  $missing=Join-Path $archives 'Not a world.zip';Zip $missing @{'README.txt'='not a world'};Reject {Call $installer 'Install' @($missing,$saves,$none)} 'Map without level.dat was installed'",
        ["map:49"]="Test-MapInstallAndRecycle.ps1:49  $cancel=[Threading.CancellationTokenSource]::new();$cancel.Cancel();try{Reject {Call $installer 'Install' @($flat,$saves,$cancel.Token)} 'Canceled map installed'}finally{$cancel.Dispose()}",
        ["map:50"]="Test-MapInstallAndRecycle.ps1:50  if(@(Get-ChildItem -LiteralPath (Join-Path $fixture 'game') -Directory -Filter '.pcl-import-*').Count){throw 'Map left incomplete staging folders'}",
        ["map:55"]="Test-MapInstallAndRecycle.ps1:55  if(@($items|Where-Object Name -eq 'PCLLFWorldDownload').Count){throw 'ZIP download directory is displayed as a world'}",
        ["update:66"]="Test-ResourceUpdates.ps1:66    if(-not $body.Contains('1.20.1') -or -not $body.Contains($(if($projectType -eq 'shader'){'iris'}else{'minecraft'})) -or -not $body.Contains('version_types')){throw 'Missing exact compatibility/channel filters'}",
        ["update:75"]="Test-ResourceUpdates.ps1:75    if(-not $url.Contains('gameVersion=1.20.1')){throw 'Missing exact CurseForge game version filter'}",
        ["update:82"]="Test-ResourceUpdates.ps1:82   throw ('Modrinth hash match or update selection failed: '+$result[0].Status+'; '+([ResourceUpdateTestLogger]::Errors -join ';'))",
        ["update:87"]="Test-ResourceUpdates.ps1:87   if((Check)[0].HasUpdate){throw 'Incompatible, foreign, prerelease or older update was accepted'}",
        ["update:90"]="Test-ResourceUpdates.ps1:90  $projectType='mod';if((Check)[0].HasUpdate){throw 'A mod was matched as a resource pack'};$projectType='resourcepack'",
        ["update:92"]="Test-ResourceUpdates.ps1:92  if((Check)[0].HasUpdate){throw 'A non-primary file was switched to another variant'}",
        ["update:95"]="Test-ResourceUpdates.ps1:95  if(-not $result[0].HasUpdate -or $result[0].UpdateVersion.Hash -cne $newHash){throw 'CurseForge exact fingerprint/hash update failed'}",
        ["update:97"]="Test-ResourceUpdates.ps1:97  if((Check)[0].HasUpdate){throw 'CurseForge fingerprint collision bypassed content hash validation'};$cfOld.hashes=$savedHash",
        ["update:99"]="Test-ResourceUpdates.ps1:99  if((Check)[0].HasUpdate){throw 'Blocked CurseForge download was reconstructed'};$cfNew.downloadUrl=$savedUrl",
        ["update:101"]="Test-ResourceUpdates.ps1:101  if((Check)[0].HasUpdate){throw 'Wrong Minecraft version accepted from CurseForge'};$cfNew.gameVersions=$savedGames",
        ["update:102"]="Test-ResourceUpdates.ps1:102  $mode='failure';if((Check)[0].Status -notmatch 'CurseForge'){throw 'API outage was presented as a complete check'}",
        ["update:105"]="Test-ResourceUpdates.ps1:105  if(-not $shaderResult[0].HasUpdate){throw 'Shader compatibility did not retain its loader set'}",
        ["update:109"]="Test-ResourceUpdates.ps1:109  if([string](Invoke-Static $updates 'FileHash' @($newFile,$false)) -cne $newHash){throw 'Built SHA1 helper differs from the ZIP checksum'}",
        ["update:110"]="Test-ResourceUpdates.ps1:110  if($result[0].UpdateVersion.Hash -cne $newHash -or $result[0].UpdateVersion.Size -ne $newSize){throw 'Selected update hash/size changed before download'}",
        ["update:112"]="Test-ResourceUpdates.ps1:112  if((Get-FileHash -LiteralPath $original -Algorithm SHA1).Hash.ToLowerInvariant() -cne $newHash){throw 'Resource update did not replace file contents'}",
        ["update:113"]="Test-ResourceUpdates.ps1:113  if([IO.File]::Exists((Join-Path $packs 'New.zip'))){throw 'Update broke the selected resource-pack filename'}",
        ["update:115"]="Test-ResourceUpdates.ps1:115  if($backup.Count -ne 1 -or (Get-FileHash -LiteralPath $backup[0] -Algorithm SHA1).Hash.ToLowerInvariant() -cne $hash){throw 'Original resource was not preserved for restore'}",
        ["update:119"]="Test-ResourceUpdates.ps1:119  Assert-Rejected {Install $result $badDownload} 'Corrupt download was accepted'",
        ["update:120"]="Test-ResourceUpdates.ps1:120  if((Get-FileHash -LiteralPath $original -Algorithm SHA1).Hash.ToLowerInvariant() -cne $hash){throw 'Failed update changed the original file'}",
        ["update:122"]="Test-ResourceUpdates.ps1:122  Assert-Rejected {Install $result $failedDownload} 'Download failure was not propagated'",
        ["update:124"]="Test-ResourceUpdates.ps1:124  Assert-Rejected {Invoke-Static $updates 'Install' @($packs,$result,$recycle,$packKind,'1.20.1',$cancel.Token,$download)} 'Cancelled update was installed';$cancel.Dispose()",
        ["update:126"]="Test-ResourceUpdates.ps1:126  Assert-Rejected {Install $result $download} 'Changed local file was replaced through a stale match'",
        ["update:127"]="Test-ResourceUpdates.ps1:127  if(@([IO.Directory]::EnumerateDirectories($packs,'.pcl-import-*')).Count -ne 0){throw 'Update staging files remained'}",
    };
    // The source IDs stay as historical coverage anchors. This product
    // expectation intentionally changed when resource recycling was retired.
    private static readonly Dictionary<string,string> CurrentPolicy = new(StringComparer.Ordinal) {
        ["update:115"] = "A successful resource update must not create a recycle copy."
    };
    private static readonly HashSet<string> Seen=new(StringComparer.Ordinal);
    internal static void Reset(string family){Seen.RemoveWhere(id=>id.StartsWith(family+":",StringComparison.Ordinal));}
    internal static void Check(string family,int line,bool condition){string id=family+":"+line;Native.Check(Original.TryGetValue(id,out string? text),"Unknown original map/update ID "+id);Native.Check(condition,CurrentPolicy.GetValueOrDefault(id,text??id));Seen.Add(id);}
    internal static void Complete(string family){string[] expected=Original.Keys.Where(id=>id.StartsWith(family+":",StringComparison.Ordinal)).ToArray();Native.Check(expected.Length>0&&Seen.Where(id=>id.StartsWith(family+":",StringComparison.Ordinal)).ToHashSet().SetEquals(expected),"Incomplete original map/update coverage: "+family+" missing="+string.Join(",",expected.Except(Seen)));Console.WriteLine("PASS original "+family+" source IDs "+expected.Length);}
}
