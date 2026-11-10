namespace PCLLF.Native.Facade.Tests;
// Fixed original LF assertions; this family never reads VB or script sources at runtime.
internal static class EnvironmentHistorySourceChecks
{
    private static readonly Dictionary<string, string> Original = new(StringComparer.Ordinal)
    {
        ["E036"] = "Test-EnvironmentHistory.ps1:36 if([bool]$point['healthy']){throw 'Untested snapshot marked healthy'}",
        ["E038"] = "Test-EnvironmentHistory.ps1:38 if([bool](Call 'Points' @($version))[0]['healthy']){throw 'Failed launch marked healthy'}",
        ["E040"] = "Test-EnvironmentHistory.ps1:40 if(-not [bool](Call 'Points' @($version))[0]['healthy']){throw 'Successful run not marked'}",
        ["E044"] = "Test-EnvironmentHistory.ps1:44 if($objectsBefore -ne $objectsAfter){throw 'Identical snapshot duplicated payloads'}",
        ["E057"] = "Test-EnvironmentHistory.ps1:57 foreach($pair in @(@('example.jar','minecraft-old'),@('mods/old.jar','mod-old'),@('config/author.toml','old-config'),@('options.txt','old-keybinds'),@('saves/world/level.dat','world-progress'),@('screenshots/keep.png','screenshot'),@('config/xaero/waypoints.txt','map-progress'))){if([IO.File]::ReadAllText((Join-Path $game $pair[0])) -cne $pair[1]){throw ('Whole environment restore mismatch: '+$pair[0])}}",
        ["E058"] = "Test-EnvironmentHistory.ps1:58 if([IO.File]::Exists((Join-Path $game 'mods/new.jar')) -or [IO.File]::Exists((Join-Path $game 'config/new.toml'))){throw 'New environment files were left behind'}",
        ["E059"] = "Test-EnvironmentHistory.ps1:59 if([IO.File]::ReadAllText($library) -cne 'library-old'){throw 'Offline dependency restoration failed'}",
        ["E060"] = "Test-EnvironmentHistory.ps1:60 if([IO.File]::ReadAllText((Join-Path $version 'example.json')) -notmatch '1.20.1' -or [IO.File]::ReadAllText((Join-Path $version 'PCL/config.json')) -notmatch 'old'){throw 'Minecraft/loader combination not restored'}",
        ["E062"] = "Test-EnvironmentHistory.ps1:62 if([IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod' -or [IO.File]::ReadAllText((Join-Path $game 'options.txt')) -cne 'new-keybinds'){throw 'Undo of whole restore failed'}",
        ["E064"] = "Test-EnvironmentHistory.ps1:64 Reject {Call 'Restore' @($version,$game,$mc,$first,$token,$fail,$null)} 'Failed restore was accepted'",
        ["E065"] = "Test-EnvironmentHistory.ps1:65 if([IO.File]::ReadAllText((Join-Path $version 'example.jar')) -cne 'minecraft-new' -or [IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod'){throw 'Failed restore did not roll back'}",
        ["E067"] = "Test-EnvironmentHistory.ps1:67 Reject {Call 'Restore' @($version,$game,$mc,$first,$cancel.Token,$null,$null)} 'Canceled restore was applied'",
        ["E068"] = "Test-EnvironmentHistory.ps1:68 Reject {Call 'Restore' @($version,(Join-Path $fixture 'other-game'),$mc,$first,$token,$null,$null)} 'Isolation mismatch was accepted'",
        ["E071"] = "Test-EnvironmentHistory.ps1:71 Reject {Call 'DeletePoints' @($version,[string[]]@($first,'../invalid'))} 'Invalid batch deletion was accepted'",
        ["E072"] = "Test-EnvironmentHistory.ps1:72 if(-not ((Call 'Points' @($version))|Where-Object {$_['id'].ToString() -eq $first})){throw 'Invalid batch partly deleted a valid point'}",
        ["E074"] = "Test-EnvironmentHistory.ps1:74 if((Call 'Points' @($version))|Where-Object {$_['id'].ToString() -eq $second}){throw 'Deleted restore point remained visible'}",
        ["E075"] = "Test-EnvironmentHistory.ps1:75 if(-not [IO.File]::Exists((Join-Path $deleted ($second+'.json')))){throw 'Deleted point record is not recoverable'}",
        ["E076"] = "Test-EnvironmentHistory.ps1:76 if(@(Get-ChildItem (Join-Path $fixture 'history') -Recurse -File|Where-Object {$_.Directory.Name -eq 'objects'}).Count -ne $blobCount){throw 'Deleting a point removed shared payloads'}",
        ["E077"] = "Test-EnvironmentHistory.ps1:77 if([IO.File]::ReadAllText((Join-Path $game 'mods/new.jar')) -cne 'added-mod'){throw 'Deleting a point modified game files'}",
        ["E083"] = "Test-EnvironmentHistory.ps1:83 Reject {Call 'Restore' @($version,$game,$mc,$first,$token,$null,$null)} 'Corrupt snapshot was restored'",
        ["E084"] = "Test-EnvironmentHistory.ps1:84 if([IO.File]::ReadAllText((Join-Path $version 'example.jar')) -cne 'minecraft-new'){throw 'Corruption rejection modified the environment'}",
        ["E095"] = "Test-EnvironmentHistory.ps1:95 if([string]::IsNullOrEmpty($adapterPoint)){throw 'Real instance snapshot adapter failed'}",
        ["T051"] = "Test-BackupRetention.ps1:51 if($settings['VersionEnvironmentHistoryAutoCleanup'].DefaultValue -ne $false){throw 'Automatic cleanup is enabled without opting in'}",
        ["T058"] = "Test-BackupRetention.ps1:58 if(@((Points)|Where-Object {-not (Manual $_)}).Count -ne 4){throw 'Disabled retention pruned existing automatic points'}",
        ["T066"] = "Test-BackupRetention.ps1:66 if($autoPoints.Count -ne 2 -or -not ($autoPoints|Where-Object {$_['id'].ToString() -eq $latest}) -or -not ($autoPoints|Where-Object {$_['id'].ToString() -eq $automatic[3]})){throw 'Retention did not keep the newest automatic points'}",
        ["T067"] = "Test-BackupRetention.ps1:67 foreach($id in @($manualOne,$manualTwo)){if(-not ($points|Where-Object {$_['id'].ToString() -eq $id})){throw 'Manual point was counted or removed'}}",
        ["T068"] = "Test-BackupRetention.ps1:68 if([IO.File]::Exists($oldBlob)){throw 'Unreferenced payload from an expired automatic point was not reclaimed'}",
        ["T069"] = "Test-BackupRetention.ps1:69 foreach($point in $points){foreach($hash in $point['files'].Properties()){if(-not [IO.File]::Exists((Join-Path $store ('objects/'+$hash.Value.ToString().Substring(5))))){throw 'A retained snapshot lost a shared payload'}}}",
        ["T074"] = "Test-BackupRetention.ps1:74 if(-not [IO.File]::Exists($deletedBlob) -or -not [IO.File]::Exists((Join-Path $deletedFolder ($removed+'.json')))){throw 'Cleanup invalidated a recoverable deleted record'}",
        ["T077"] = "Test-BackupRetention.ps1:77 Reject {Call $history 'CaptureInstance' @($instance,'游戏启动前',$cancel.Token)} 'Canceled capture succeeded'",
        ["T078"] = "Test-BackupRetention.ps1:78 Reject {Call $history 'PruneAutomatic' @($instance.PathVersion,0,$latest,$none)} 'Invalid limit was accepted'",
        ["T079"] = "Test-BackupRetention.ps1:79 if((Points).Count -ne $count){throw 'Failed capture or invalid limit removed a restore point'}",
        ["T086"] = "Test-BackupRetention.ps1:86 if([IO.File]::ReadAllText((Join-Path $version 'mods/library.jar')) -cne $expected){throw 'Cleanup removed the selected target during restore'}",
        ["T088"] = "Test-BackupRetention.ps1:88 if(@((Points)|Where-Object {-not (Manual $_)}).Count -ne 1 -or -not ((Points)|Where-Object {$_['id'].ToString() -eq $undo})){throw 'Post-restore cleanup discarded the undo snapshot'}",
        ["T090"] = "Test-BackupRetention.ps1:90 if([IO.File]::ReadAllText((Join-Path $version 'mods/library.jar')) -cne 'before-restore'){throw 'Retained undo point is not restorable'}",
        ["T093"] = "Test-BackupRetention.ps1:93 Reject {Call $history 'PruneAutomatic' @($instance.PathVersion,1,$undo,$none)} 'Malformed references were ignored during garbage collection'",
        ["T094"] = "Test-BackupRetention.ps1:94 if((Points).Count -ne $count){throw 'Invalid references caused partial pruning'}",
        ["T100"] = "Test-BackupRetention.ps1:100 if($page.FindName('CheckAutoCleanup').Checked -or $page.FindName('PanRetention').Visibility.ToString() -ne 'Collapsed'){throw 'Cleanup opt-in UI is not collapsed when off'}",
        ["T103"] = "Test-BackupRetention.ps1:103 if(-not $settings['VersionEnvironmentHistoryAutoCleanup'].ValueCache[$instance.PathVersion] -or $page.FindName('PanRetention').Visibility.ToString() -ne 'Visible' -or (Points).Count -ne $count){throw 'Opt-in did not persist or deleted points immediately'}",
        ["T105"] = "Test-BackupRetention.ps1:105 if(-not (Call $page 'CommitRetention' @()) -or $settings['VersionEnvironmentHistoryRetention'].ValueCache[$instance.PathVersion] -ne 3){throw 'Retention limit was not persisted for its instance'}",
        ["T107"] = "Test-BackupRetention.ps1:107 if((Call $page 'CommitRetention' @()) -or $page.FindName('TextRetention').Text -ne '3'){throw 'Invalid UI limit was persisted'}",
        ["R034"] = "Test-RecentEnvironmentChanges.ps1:34 if($empty.HasBaseline -or $empty.ToAnalysisText() -notmatch '尚无正常运行记录'){throw 'No-baseline state is misleading'}",
        ["R038"] = "Test-RecentEnvironmentChanges.ps1:38 if(-not $unchanged.HasBaseline -or $unchanged.BaselineId -cne $healthy -or $unchanged.Changes.Count -ne 0){throw 'Unchanged baseline mismatch'}",
        ["R054"] = "Test-RecentEnvironmentChanges.ps1:54 if($report.BaselineId -cne $healthy -or $report.BaselineId -ceq $unhealthy -or -not $report.BaselineCreatedUtc){throw 'Latest healthy baseline was not selected'}",
        ["R055"] = "Test-RecentEnvironmentChanges.ps1:55 Assert-Change $report 'g/mods/disabled.jar.disabled' '禁用'",
        ["R056"] = "Test-RecentEnvironmentChanges.ps1:56 Assert-Change $report 'g/mods/re-enabled.jar' '^启用'",
        ["R057"] = "Test-RecentEnvironmentChanges.ps1:57 Assert-Change $report 'g/mods/update-2.0.jar' '文件版本变化'",
        ["R058"] = "Test-RecentEnvironmentChanges.ps1:58 Assert-Change $report 'g/mods/content.jar' '更新内容'",
        ["R059"] = "Test-RecentEnvironmentChanges.ps1:59 Assert-Change $report 'g/mods/added.jar.disabled' '新增'",
        ["R060"] = "Test-RecentEnvironmentChanges.ps1:60 Assert-Change $report 'g/config/private.toml' '修改'",
        ["R061"] = "Test-RecentEnvironmentChanges.ps1:61 Assert-Change $report 'g/config/deleted.toml' '删除'",
        ["R062"] = "Test-RecentEnvironmentChanges.ps1:62 Assert-Change $report 'g/config/new.toml' '新增'",
        ["R063"] = "Test-RecentEnvironmentChanges.ps1:63 Assert-Change $report 'v/example.json' '修改'",
        ["R065"] = "Test-RecentEnvironmentChanges.ps1:65 if($text -match 'fixture-private|api_key|original-world|world-changed|shot-changed|waypoints|markers-changed' -or $text.Contains($fixture)){throw 'Report exposed configuration/account/personal values or absolute paths'}",
        ["R066"] = "Test-RecentEnvironmentChanges.ps1:66 if($text -notmatch '不能据此确定崩溃原因' -or $text -notmatch '1.0' -or $text -notmatch '2.0'){throw 'Report lacks uncertainty or version information'}",
        ["R068"] = "Test-RecentEnvironmentChanges.ps1:68 if(-not $appended.StartsWith('fixture crash reason') -or $appended -notmatch '上次正常运行后改了什么'){throw 'Crash analysis appendix failed'}",
        ["R071"] = "Test-RecentEnvironmentChanges.ps1:71 if($mismatch.HasBaseline -or $mismatch.ToAnalysisText() -notmatch '不同的游戏目录'){throw 'Isolation mismatch produced a false comparison'}",
        ["R073"] = "Test-RecentEnvironmentChanges.ps1:73 Reject {Invoke-Engine $changes 'Build' @($version,$game,$cancel.Token)} 'Canceled comparison completed'",
        ["R076"] = "Test-RecentEnvironmentChanges.ps1:76 Reject {Invoke-Engine $changes 'Build' @($version,$game,$token)} 'Linked config directory was scanned'",
        ["R078"] = "Test-RecentEnvironmentChanges.ps1:78 if([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -cne 'keep-outside'){throw 'Comparison modified external linked data'}",
        ["C030"] = "Test-EnvironmentCoverage.ps1:30 foreach($folder in $extra){$file=Join-Path $legacy ($folder+'/preserve.dat');if(-not [IO.File]::Exists($file) -or [IO.File]::ReadAllText($file) -cne ('untracked-'+$folder)){throw ('Old point deleted an originally untracked directory: '+$folder)}}",
        ["C031"] = "Test-EnvironmentCoverage.ps1:31 if([IO.File]::ReadAllText((Join-Path $legacy 'config/setting.toml')) -cne 'old-setting' -or [IO.File]::Exists((Join-Path $legacy 'mods/added.jar'))){throw 'Legacy tracked files did not restore normally'}",
        ["C033"] = "Test-EnvironmentCoverage.ps1:33 if($report.Changes.Count -ne 0){throw 'Legacy crash comparison reports untracked directories as new changes'}",
        ["C037"] = "Test-EnvironmentCoverage.ps1:37 if($newPoint['coverageVersion'].ToObject([int]) -ne 2){throw 'New point lacks explicit extended coverage'}",
        ["C041"] = "Test-EnvironmentCoverage.ps1:41 foreach($folder in $extra){if(-not @($newReport.Changes|Where-Object {$_.Relative -eq ('g/'+$folder+'/added.dat')}).Count){throw ('New crash comparison omitted '+$folder)}}",
        ["C043"] = "Test-EnvironmentCoverage.ps1:43 foreach($folder in $extra){if([IO.File]::Exists((Join-Path $modern ($folder+'/added.dat')))){throw ('New point failed to restore an explicitly covered empty directory: '+$folder)}}",
        ["C046"] = "Test-EnvironmentCoverage.ps1:46 Reject {Call $history 'Restore' @($modern,$modern,$mc,$newId,$token,$null,$null)} 'Unknown coverage version was guessed'",
        ["C047"] = "Test-EnvironmentCoverage.ps1:47 if([IO.File]::ReadAllText((Join-Path $modern 'coremods/preserve-unknown.jar')) -cne 'do-not-touch'){throw 'Unknown coverage modified the environment'}",
    };
    // Historical source IDs remain anchors; these expectations follow the
    // approved permanent-deletion policy instead of preserving deleted records.
    private static readonly Dictionary<string,string> CurrentPolicy = new(StringComparer.Ordinal) {
        ["E075"] = "Deleted restore-point records must be removed permanently without a deleted directory.",
        ["E076"] = "Reclaim unreferenced objects while preserving every object referenced by remaining points.",
        ["T074"] = "Deleted restore-point records and unique objects must not remain recoverable."
    };
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    internal static void Reset() => Seen.Clear();
    internal static void Check(string id, bool condition)
    {
        Native.Check(Original.TryGetValue(id, out string? original), "Unknown original environment assertion: " + id);
        Native.Check(condition, id + " " + CurrentPolicy.GetValueOrDefault(id, original!));
        Seen.Add(id);
    }
    internal static void Complete()
    {
        Native.Check(Seen.SetEquals(Original.Keys), "Original environment assertion coverage missing=" + string.Join(',', Original.Keys.Except(Seen)));
        Console.WriteLine("PASS environment-history original assertions " + Seen.Count + "/" + Original.Count);
    }
}
