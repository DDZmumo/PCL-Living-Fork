#requires -Version 5.1
param(
 [Parameter(Mandatory=$true)][string]$NativeExecutablePath,
 [Parameter(Mandatory=$true)][string]$BridgeExecutablePath,
 [Parameter(Mandatory=$true)][string]$LegacyExecutablePath,
 [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedLegacySha256,
 [string]$LegacyArtifactArchive,
 [string]$WorkspaceRoot=$env:GITHUB_WORKSPACE,
 [string]$EvidenceRoot,
 [string]$DotNetPath='dotnet',
 [string]$NativeProjectPath,
 [string]$SmokeSupportRoot,
 [ValidateSet('legacy','manual','parent-timeout','invalid-inputs','native-reinstall','restart-rollback')][string[]]$Cases=@('legacy','manual','parent-timeout','invalid-inputs'),
 [ValidateRange(15,120)][int]$StartupTimeoutSeconds=60
)
# Genuine entry acceptance only. No replacement Main, removed Startup, extra
# protocol argument, update download acceptance, real account or external release.
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
if($env:OS -ne 'Windows_NT' -or $env:CI -ne 'true' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'PENDING: requires a fresh disposable GitHub-hosted Windows CI machine/profile.'}
if([IntPtr]::Size -ne 8 -or $PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'PENDING: use Windows PowerShell 5.1 x64 -NoProfile -STA.'}
if(-not $WorkspaceRoot){throw 'WorkspaceRoot is required.'}
$WorkspaceRoot=[IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
if(-not [IO.Directory]::Exists($WorkspaceRoot) -or $WorkspaceRoot -match '[^\x00-\x7f]' -or [string]::Equals($WorkspaceRoot,[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -or $WorkspaceRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $WorkspaceRoot.IndexOf('AppData\Local\Temp\',[StringComparison]::OrdinalIgnoreCase) -ge 0){throw 'PENDING: use a real ASCII CI workspace outside TEMP; preserve original LF protection.'}
if(@($Cases|Select-Object -Unique).Count -ne $Cases.Count -or $Cases.Count -eq 0){throw 'Cases must select a non-empty unique acceptance set.'}
if(-not $EvidenceRoot){$EvidenceRoot=Join-Path $WorkspaceRoot 'artifacts/native-upgrade'}
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
if(-not $EvidenceRoot.StartsWith($WorkspaceRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'EvidenceRoot must remain in the dedicated workspace.'}
if(-not $NativeProjectPath){$NativeProjectPath=Join-Path $repoRoot 'modern/Main/PCL-Living-Fork.Modern.csproj'}
if(-not $SmokeSupportRoot){$SmokeSupportRoot=Join-Path $PSScriptRoot 'native-smoke'}
. (Join-Path $PSScriptRoot 'native-upgrade/ReviewedUpgradeNotices.ps1')
& (Join-Path $PSScriptRoot 'native-upgrade/Test-ReviewedUpgradeNotices.ps1') -UpgradeScriptPath $PSCommandPath
foreach($name in @('NativeExecutablePath','BridgeExecutablePath','LegacyExecutablePath','NativeProjectPath')){Set-Variable -Name $name -Value ([IO.Path]::GetFullPath((Get-Variable -Name $name -ValueOnly)));if(-not [IO.File]::Exists((Get-Variable -Name $name -ValueOnly))){throw ('Missing input '+$name)}}
$id=[Guid]::NewGuid().ToString('N');$fixture=Join-Path $EvidenceRoot ('NativeUpgrade-'+$id);$marker='__PclLfNativeUpgradeOwner'
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedRegistry=[Collections.Generic.List[string]]::new();$ownedAppData=$null;$allStopped=$true;$aclTarget=$null;$originalAcl=$null;$probe=$null
$evidence=[ordered]@{Result='pending';FixtureId=$id;Cases=@();ForcedCleanup=$false;CleanupErrors=@();Scope='Actual old Main/canonical bridge/actual native entry; selected protocol cases only';LegacySource=@{RunId='41894581233';ArtifactId='13354809865';Head='8ff80deb1de834efbee2fc6b180cc268dc1eed0e';ArtifactName='PCL-Living-Fork Release 8ff80de';ExeFileName='PCL-Living-Fork-1.3.4.exe';ExeBytes=10266112;ArchiveSha256='c0583c65e294e20c2c781ea7842f817ac8c57c84cb71cbb7d99a171392883bc3';ExpectedExeSha256=$ExpectedLegacySha256.ToLowerInvariant()};Cancellation='DownloadUpdate transport OCE is a separate actual production facade case. The four-argument helper has no cancel argument.'}
function Pending([string]$reason){$evidence.Result='pending';throw ('PENDING: '+$reason)}
function Hash-File([string]$path){$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose();$stream.Dispose()}}
function Existing-Key([string]$name){foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){$base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view);try{$key=$base.OpenSubKey($name,$false);if($null -ne $key){$key.Dispose();return $true}}finally{$base.Dispose()}};return $false}
function Existing-Mutex([string]$name){try{$gate=[Threading.Mutex]::OpenExisting($name);$gate.Dispose();return $true}catch [Threading.WaitHandleCannotBeOpenedException]{return $false}}
function Check-Mutex([bool]$held){foreach($name in @('PCL_SingletonMutex','LivingPCL_SingletonMutex')){$gate=$null;$acquired=$false;try{try{$gate=[Threading.Mutex]::OpenExisting($name)}catch [Threading.WaitHandleCannotBeOpenedException]{if($held){throw ('Actual Main has no '+$name)};continue};try{$acquired=$gate.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true;if($held){throw 'Actual running Main abandoned its lock.'}};if($held -eq $acquired){throw ('Wrong real session lock state: '+$name)}}finally{if($null -ne $gate){if($acquired){$gate.ReleaseMutex()};$gate.Dispose()}}}}
# FileLogger initializes on its own thread and rotates Log1 after process start.
# Keep byte identity distinct from the PID whose ready/close receipts we inspect.
function Read-LogSnapshot([IO.Stream]$stream=$null) {
 $ownsStream=$false;$copy=$null
 if($null -eq $stream){
  try{$stream=[IO.File]::Open($script:log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$ownsStream=$true}
  catch [IO.FileNotFoundException]{return ,([byte[]]@())}
  catch [IO.DirectoryNotFoundException]{return ,([byte[]]@())}
 }
 try{$stream.Position=0;$copy=[IO.MemoryStream]::new();$stream.CopyTo($copy);return ,$copy.ToArray()}
 finally{if($null -ne $copy){$copy.Dispose()};if($ownsStream){$stream.Dispose()}}
}
function Convert-LogSnapshot([byte[]]$bytes) {
 $stream=[IO.MemoryStream]::new($bytes,$false);$reader=$null
 try{$reader=[IO.StreamReader]::new($stream);return $reader.ReadToEnd()}
 finally{if($null -ne $reader){$reader.Dispose()}else{$stream.Dispose()}}
}
function Hash-LogSnapshot([byte[]]$bytes) {
 $hash=[Security.Cryptography.SHA256]::Create()
 try{return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()}
 finally{$hash.Dispose()}
}
function Test-ClosedLogGeneration([byte[]]$bytes,[int]$processId) {
 # Only a DIFFERENT PID's already validated, normally closed exact bytes can be
 # awaiting rotation. Changed/mixed logs still face the one current notice.
 return $null -ne $script:closedLogGeneration -and $script:closedLogGeneration.ProcessId -ne $processId -and
  $bytes.Length -eq $script:closedLogGeneration.Bytes.Length -and
  [Convert]::ToBase64String($bytes) -ceq [Convert]::ToBase64String($script:closedLogGeneration.Bytes)
}
function Check-Log([string]$text,[string]$notice,[switch]$RequireComplete){$completeNotice=Test-ReviewedStartupNotice -Text $text -Notice $notice -NativeVersion $nativeProof.versionBaseName -NativePhase:($notice -ceq $newNotice) -NativeStyle $nativeProof.startupNoticeStyle -NativeSource $nativeProof.startupNoticeSource;if($RequireComplete -and -not $completeNotice){throw 'Actual Main final close has no complete reviewed startup notice.'}}
function Start-Owned([string]$path,[string]$arguments=''){$info=[Diagnostics.ProcessStartInfo]::new($path,$arguments);$info.UseShellExecute=$false;$info.WorkingDirectory=$script:differentCwd;$info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $fixture 'BundleExtract';$info.EnvironmentVariables['PCL_UPDATE_BRIDGE_NO_UI']='1';$process=[Diagnostics.Process]::Start($info);if($null -eq $process){throw 'Owned actual entry did not start.'};$ownedProcesses.Add($process);return $process}
function Track-Process([int]$processId){$process=[Diagnostics.Process]::GetProcessById($processId);try{[IntPtr]$trackedHandle=$process.Handle;if($trackedHandle -eq [IntPtr]::Zero){throw 'Tracked actual entry has no retained process handle.'};$ownedProcesses.Add($process);return $process}catch{$process.Dispose();throw}}
function Assert-OwnedProcessImage([object]$image,[string]$expectedPath){
 if(-not $image.QuerySucceeded){throw ('QueryFullProcessImageNameW failed for the actual process handle: Win32='+$image.Win32Error+'; '+$image.QueryErrorType+'; '+$image.QueryError)}
 if([string]::IsNullOrEmpty($image.Path) -or -not [string]::Equals($image.Path,$expectedPath,[StringComparison]::OrdinalIgnoreCase)){throw ('Actual process image differs from owned target. Expected='+$expectedPath+'; QueryFullProcessImageNameW='+$image.Path+'; MainModule(diagnostic only)='+$image.LegacyMainModulePath)}
}
function Wait-Ready([Diagnostics.Process]$process,[string]$notice){$watch=[Diagnostics.Stopwatch]::StartNew();$stable=-1;$window=$null;while($watch.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds){$process.Refresh();if($process.HasExited){throw ('Actual Main exited before ready: '+$process.ExitCode)};$imageKey='ImageIdentityFirst-'+$process.Id;$firstImage=-not $script:caseRecord.Contains($imageKey);$image=[PclLfNativeSmoke.Win32]::ObserveProcessImage($process,$firstImage);if($firstImage){$script:caseRecord[$imageKey]=$image};if($firstImage -or -not $image.QuerySucceeded -or -not [string]::Equals($image.Path,$script:target,[StringComparison]::OrdinalIgnoreCase)){$imageDiagnostic=[ordered]@{Source='QueryFullProcessImageNameW/process.Handle/flags0';ExpectedPath=$script:target;ProcessId=$process.Id;ElapsedMilliseconds=$watch.ElapsedMilliseconds;FirstSample=$script:caseRecord[$imageKey];CurrentSample=$image};[IO.File]::WriteAllText((Join-Path $fixture ('process-image-'+$process.Id+'-diagnostics.json')),($imageDiagnostic|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false));Write-Output ('Actual upgrade Main process image diagnostic: '+($imageDiagnostic|ConvertTo-Json -Depth 5 -Compress))};Assert-OwnedProcessImage $image $script:target;$windows=@([PclLfNativeSmoke.Win32]::Windows($process.Id));foreach($row in $windows){if($row.Title.Length -gt 0 -and ($row.ClassName -notlike 'HwndWrapper*' -or $row.Title.Trim() -cne 'LivingPCL')){Pending ('Unexpected actual window: '+$row.Title+' / '+$row.ClassName)}};$window=@($windows|Where-Object {$_.Title.Trim() -ceq 'LivingPCL' -and $_.ClassName -like 'HwndWrapper*' -and $_.Right -gt $_.Left -and $_.Bottom -gt $_.Top})|Select-Object -First 1;[byte[]]$logBytes=Read-LogSnapshot;if(Test-ClosedLogGeneration $logBytes $process.Id){$generationKey='LogGenerationWait-'+$process.Id;if(-not $script:caseRecord.Contains($generationKey)){$script:caseRecord[$generationKey]=[ordered]@{PriorProcessId=$script:closedLogGeneration.ProcessId;CurrentProcessId=$process.Id;PriorNotice=$script:closedLogGeneration.Notice;Bytes=$logBytes.Length;Sha256=$script:closedLogGeneration.Sha256;Status='Exact normally closed prior log; waiting for current logger rotation, never ready'}};$stable=-1;Start-Sleep -Milliseconds 100;continue};$text=Convert-LogSnapshot $logBytes;Check-Log $text $notice;$ready=$null -ne $window -and $text.Contains('第一阶段加载用时：') -and $text.Contains('第二阶段加载用时：') -and $text.Contains('第三阶段加载用时：') -and $text.Contains('PCL 位置：') -and $text.Contains($script:target) -and (Test-ReviewedStartupNotice -Text $text -Notice $notice -NativeVersion $nativeProof.versionBaseName -NativePhase:($notice -ceq $newNotice) -NativeStyle $nativeProof.startupNoticeStyle -NativeSource $nativeProof.startupNoticeSource);if($ready){if($stable -lt 0){$stable=$watch.ElapsedMilliseconds};if($watch.ElapsedMilliseconds-$stable -ge 1500){break}}else{$stable=-1};Start-Sleep -Milliseconds 100};if($stable -lt 0 -or $watch.ElapsedMilliseconds-$stable -lt 1500){Pending 'Actual product did not reach stable HWND/log/animation checkpoint.'};Check-Mutex $true;return $window}
function Assert-LfNormalClose([int]$exitCode,[string]$text,[string]$expectedNotice){
 # LF50 EndProgramForce(Success=0) flushes and self-Kill()s. The fixed Windows
 # runtime10.0.14 Process.Kill calls TerminateProcess(handle,-1), not Exit(0).
 if($exitCode -ne -1){throw ('Actual Main did not preserve LF Windows self-Kill exit -1; observed '+$exitCode)}
 Check-Log $text $expectedNotice -RequireComplete
 $closing=[regex]::Match($text,'(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I [^\r\n]*\] 收到关闭指令\r?$')
 $success=[regex]::Match($text,'(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I [^\r\n]*\] 程序已退出，返回值：Success\r?$')
 if(-not $closing.Success -or -not $success.Success -or $success.Index -le $closing.Index){throw 'Actual Main lacks ordered real LF closing and Success termination log receipts.'}
}
function Close-Main([Diagnostics.Process]$process,[string]$notice) {
 $window=Wait-Ready $process $notice
 # Capture the actual old file BEFORE WM_CLOSE. A real updater may already
 # start its new Main/logger while the observer is checking old close receipts.
 # Read-only RW|Delete sharing does not block the original copy/delete rotation.
 $closedHandle=[IO.File]::Open($script:log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
 try{
  $script:installCloseWatch=[Diagnostics.Stopwatch]::StartNew()
  if(-not [PclLfNativeSmoke.Win32]::PostClose($window.Handle)){throw 'Actual Main WM_CLOSE failed.'}
  $remaining=[Math]::Max(0,15000-[int]$script:installCloseWatch.ElapsedMilliseconds)
  if($remaining -le 0 -or -not $process.WaitForExit($remaining)){throw 'Actual Main failed normal bounded closing.'}
  [byte[]]$closedBytes=Read-LogSnapshot $closedHandle
  $text=Convert-LogSnapshot $closedBytes
  Assert-LfNormalClose $process.ExitCode $text $notice;Check-Mutex $false
  $script:closedLogGeneration=[pscustomobject]@{ProcessId=$process.Id;Notice=$notice;Bytes=$closedBytes;Sha256=(Hash-LogSnapshot $closedBytes)}
  if(-not $script:caseRecord.Contains('ClosedLogGenerations')){$script:caseRecord.ClosedLogGenerations=@()}
  $script:caseRecord.ClosedLogGenerations+=,[ordered]@{ProcessId=$process.Id;Notice=$notice;Bytes=$closedBytes.Length;Sha256=$script:closedLogGeneration.Sha256;NormalExit=$true;ExitCode=$process.ExitCode;Source='Read-only Log1 handle opened before WM_CLOSE'}
 }finally{$closedHandle.Dispose()}
}
function Check-Install {if([IO.File]::ReadAllText((Join-Path $script:install '.minecraft/Upgrade.sentinel')) -cne $id -or -not [IO.File]::ReadAllText((Join-Path $script:install 'PCL/Setup.ini')).Contains('NativeUpgradeSentinel:'+$id)){throw 'Update/startup lost synthetic settings/game sentinel.'};foreach($pattern in @(([IO.Path]::GetFileName($script:target)+'.updating-*'),([IO.Path]::GetFileName($script:target)+'.update-backup-*'))){if(@([IO.Directory]::EnumerateFiles($script:install,$pattern)).Count -ne 0){throw 'Actual updater retained temporary replacement/backup files.'}};Check-InstallFileRelease}
function Check-InstallFileRelease([string]$Path=$script:target,[ValidatePattern('\A(?:installed-target|extracted-helper|staged-[0-9]+)\z')][string]$Role='installed-target') {
 # Normal entry cases retain the final WM_CLOSE deadline. Invalid-input and
 # rollback cases have no running Main at their final helper-stop checkpoint;
 # their bounded verification starts here, never restarts a Main close budget.
 $budgetSource='WM_CLOSE';$watch=$script:installCloseWatch
 if($script:caseRecord.Name -in @('invalid-inputs','restart-rollback')){if($null -eq $script:helperStopVerificationWatch){$script:helperStopVerificationWatch=[Diagnostics.Stopwatch]::StartNew()};$watch=$script:helperStopVerificationWatch;$budgetSource='helper-stopped verification'}
 if($null -eq $watch){throw 'No actual close/helper-stop checkpoint for installed file release.'}
 $release=[PclLfNativeSmoke.FileRelease]::WaitForExclusiveRelease($Path,$watch,15000)
 $observation=@{Role=$Role;BudgetSource=$budgetSource;Observation=$release}
 if($Role -ceq 'installed-target'){$script:caseRecord.ExclusiveFileRelease=$observation}
 else{if(-not $script:caseRecord.Contains('StageFileReleases')){$script:caseRecord.StageFileReleases=@()};$script:caseRecord.StageFileReleases+=,$observation}
 [IO.File]::WriteAllText((Join-Path $fixture ('file-release-'+$script:caseRecord.Name+'-'+$Role+'-diagnostics.json')),($observation|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
 Write-Output ('Actual upgrade exclusive file release: '+($observation|ConvertTo-Json -Depth 8 -Compress))
 if(-not $release.Succeeded){throw ('Actual owned '+$Role+' failed exclusive ReadWrite/FileShare.None release: '+$release.ErrorType+'; Win32='+$release.LastWin32Error+'; HResult='+$release.LastHResult+'; '+$release.Error)}
}
function New-Install([string]$caseName,[string]$source){$script:installCloseWatch=$null;$script:helperStopVerificationWatch=$null;$script:closedLogGeneration=$null;$script:install=Join-Path $fixture ($caseName+' Install with spaces');$script:differentCwd=Join-Path $fixture ($caseName+' Different cwd');$script:target=Join-Path $script:install 'LivingPCL Actual Target.exe';$script:log=Join-Path $script:install 'PCL/Log1.txt';foreach($directory in @($script:install,$script:differentCwd,(Join-Path $script:install '.minecraft/versions'),(Join-Path $script:install 'PCL/Updates'))){[IO.Directory]::CreateDirectory($directory)|Out-Null};$cache=Join-Path $script:install 'OwnCache';$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCL',$true);try{$key.SetValue('SystemSystemCache',$cache+'\',[Microsoft.Win32.RegistryValueKind]::String);$key.SetValue('LaunchFolders','Upgrade Fixture>'+[IO.Path]::Combine($script:install,'.minecraft')+'\',[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()};[IO.File]::WriteAllText((Join-Path $script:install '.minecraft/Upgrade.sentinel'),$id);[IO.File]::WriteAllText((Join-Path $script:install 'PCL/Setup.ini'),('NativeUpgradeSentinel:'+$id+"`r`nLaunchFolderSelect:"+(Join-Path $script:install '.minecraft')+"\`r`nUiLauncherLogo:False`r`nUiCustomType:0`r`nLivingModpackMode:False`r`nToolLauncherUpdateSource:0`r`nToolLauncherUpdateChannel:0`r`n"),[Text.UTF8Encoding]::new($false));Copy-Item -LiteralPath $source -Destination $script:target;if([IO.File]::Exists($source+'.config')){Copy-Item -LiteralPath ($source+'.config') -Destination ($script:target+'.config')};$script:caseRecord=[ordered]@{Name=$caseName;Result='pending';Target=$script:target;OriginalSha256=(Hash-File $script:target)};$evidence.Cases+=,$script:caseRecord}
function Wait-Helper([Diagnostics.Process]$bridge,[int]$parentId){$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt 10){$rows=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$bridge.Id)|Where-Object {$_.ExecutablePath -and [IO.Path]::GetDirectoryName($_.ExecutablePath) -ieq (Join-Path $script:install 'PCL/Updates') -and [IO.Path]::GetFileName($_.ExecutablePath) -match '^pcl-fork-[a-f0-9]{32}\.exe$'});if($rows.Count -gt 1){throw 'Bridge spawned multiple matching native payloads.'};if($rows.Count -eq 1){$row=$rows[0];if((Hash-File $row.ExecutablePath) -cne $nativeProof.sha256){throw 'Extracted helper is not exact actual native Main.'};$arguments=[PclLfNativeUpgrade.Dialogs]::Arguments($row.CommandLine);if($arguments.Count -ne 5 -or $arguments[1] -cne '--livingpcl-update' -or $arguments[2] -cne [string]$parentId -or $arguments[3] -cne [IO.Path]::GetFileName($script:target) -or $arguments[4] -cne $nativeProof.sha256){throw 'Actual child did not retain four args/parent/target/native payload hash.'};$script:caseRecord.Helper=@{ProcessId=$row.ProcessId;Path=$row.ExecutablePath;Arguments=$arguments;Sha256=$nativeProof.sha256};return (Track-Process $row.ProcessId)};Start-Sleep -Milliseconds 50};Pending 'Did not observe actual bridge native child/argv within the bound.'}
function Wait-Restart { $watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt 15){$rows=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -and [string]::Equals($_.ExecutablePath,$script:target,[StringComparison]::OrdinalIgnoreCase)});if($rows.Count -gt 1){throw 'Multiple actual target restarts.'};if($rows.Count -eq 1){return (Track-Process $rows[0].ProcessId)};Start-Sleep -Milliseconds 100};Pending 'Actual updater did not restart the installed Main target.'}
function Wait-Failure([Diagnostics.Process]$helper,[string]$requiredText){$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt 35){$helper.Refresh();if($helper.HasExited){throw 'Native helper exited before expected real failure evidence.'};$windows=@([PclLfNativeSmoke.Win32]::Windows($helper.Id));foreach($window in $windows){if($window.Title -ceq 'PCL Living Fork 更新失败' -and $window.ClassName -ceq '#32770'){$body=[PclLfNativeUpgrade.Dialogs]::Body($window.Handle);if(-not $body.Contains('自动更新未完成，请从发布页手动更新。') -or ($requiredText -and -not $body.Contains($requiredText))){throw ('Unexpected genuine helper failure: '+$body)};$script:caseRecord.Failure=@{Title=$window.Title;Body=$body;ElapsedMilliseconds=$watch.ElapsedMilliseconds};if(-not [PclLfNativeUpgrade.Dialogs]::DeclineFailure($window.Handle)){Pending 'Owned updater failure dialog has no identifiable enabled No button.'};if(-not $helper.WaitForExit(10000) -or $helper.ExitCode -ne 0){throw 'Native helper failed original failure-handler exit contract.'};return}};Start-Sleep -Milliseconds 100};Pending 'No identifiable genuine helper failure dialog within bound.'}
try {
 [IO.Directory]::CreateDirectory($fixture)|Out-Null;[IO.File]::WriteAllText((Join-Path $fixture 'owner.txt'),$id)
 if($ExpectedLegacySha256.ToLowerInvariant() -cne 'f4c88a842d506c62e812d50493126c5f25a73b0bfa866bb749c0473e2740d151' -or (Hash-File $LegacyExecutablePath) -cne $ExpectedLegacySha256.ToLowerInvariant()){throw 'Pinned genuine 8ff80de legacy input SHA mismatch.'}
 if($LegacyArtifactArchive){if((Hash-File $LegacyArtifactArchive) -cne $evidence.LegacySource.ArchiveSha256){throw 'Wrong original artifact ZIP; expected fixed 41894581233/13354809865.'};Add-Type -AssemblyName System.IO.Compression.FileSystem;$zip=[IO.Compression.ZipFile]::OpenRead($LegacyArtifactArchive);try{$entries=@($zip.Entries|Where-Object {$_.FullName.EndsWith('.exe',[StringComparison]::OrdinalIgnoreCase)});if($entries.Count -ne 1){throw 'Pinned old artifact must contain exactly one EXE.'};$stream=$entries[0].Open();$hash=[Security.Cryptography.SHA256]::Create();try{$zipHash=[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose();$stream.Dispose()};if($zipHash -cne $ExpectedLegacySha256.ToLowerInvariant()){throw 'Legacy input differs from pinned artifact EXE bytes.'};$evidence.LegacySource.ArchiveVerified=$true;$evidence.LegacySource.Entry=$entries[0].FullName}finally{$zip.Dispose()}}else{$evidence.LegacySource.ArchiveVerified=$false;$evidence.LegacySource.ProvenanceLimit='Exact EXE SHA is hard-locked to the root-verified pinned artifact. The optional raw ZIP was not provided for archive digest re-verification in this job.'}
 $tools=Join-Path $fixture 'Tools'
 $descriptorObj=Join-Path $fixture 'DescriptorObj';$legacyObj=Join-Path $fixture 'LegacyObj';$nativeObj=Join-Path $fixture 'NativeObj'
 & $DotNetPath build (Join-Path $PSScriptRoot 'native-upgrade/NativeUpdateFixture.csproj') -c Release --output (Join-Path $tools 'Descriptor') ('-p:BaseIntermediateOutputPath='+$descriptorObj+'\') ('-p:MSBuildProjectExtensionsPath='+$descriptorObj+'\') -warnaserror -v:q
 if($LASTEXITCODE -ne 0){throw 'Descriptor strict build failed.'}
 & $DotNetPath build (Join-Path $PSScriptRoot 'native-upgrade/LegacyBridgeValidation.csproj') -c Release --output (Join-Path $tools 'LegacyValidation') ('-p:BaseIntermediateOutputPath='+$legacyObj+'\') ('-p:MSBuildProjectExtensionsPath='+$legacyObj+'\') -warnaserror -v:q
 if($LASTEXITCODE -ne 0){throw 'Actual old validator host strict build failed.'}
 & $DotNetPath build (Join-Path $SmokeSupportRoot 'NativeSmokeFixture.csproj') -c Release --output (Join-Path $tools 'NativeInspector') ('-p:BaseIntermediateOutputPath='+$nativeObj+'\') ('-p:MSBuildProjectExtensionsPath='+$nativeObj+'\') ('-p:LFNativeUpdaterSource='+[IO.Path]::Combine((Split-Path $NativeProjectPath -Parent),'Modules/ModernBundleIdentity.cs')) -warnaserror -v:q
 if($LASTEXITCODE -ne 0){throw 'Actual native entry inspector strict build failed.'}
 & $DotNetPath (Join-Path $tools 'NativeInspector/NativeSmokeFixture.dll') --self-test-notice-metadata
 if($LASTEXITCODE -ne 0){throw 'Metadata-only startup notice route regression failed.'}
 $json=& $DotNetPath (Join-Path $tools 'NativeInspector/NativeSmokeFixture.dll') $NativeExecutablePath;if($LASTEXITCODE -ne 0){throw 'Not genuine actual native PCL.Application.Main/full resources.'};$nativeProof=($json -join "`n")|ConvertFrom-Json
 if($nativeProof.buildType -notin @(0,50)){Pending 'Only actual Release/Snapshot builds are reviewed for entry execution.'}
 $json=& $DotNetPath (Join-Path $tools 'Descriptor/NativeUpdateFixture.dll') legacy $LegacyExecutablePath;if($LASTEXITCODE -ne 0){throw 'Not genuine actual legacy Main.'};$legacyProof=($json -join "`n")|ConvertFrom-Json
 if($legacyProof.details.versionBaseName -cne '1.3.4'){throw 'Pinned old product version is not actual 1.3.4.'}
 $json=& $DotNetPath (Join-Path $tools 'Descriptor/NativeUpdateFixture.dll') bridge $BridgeExecutablePath $nativeProof.sha256;if($LASTEXITCODE -ne 0){throw 'Managed bridge payload/hash/entry identity mismatch.'};$bridgeProof=($json -join "`n")|ConvertFrom-Json
 $evidence.NativeProof=$nativeProof;$evidence.LegacyProof=$legacyProof;$evidence.BridgeProof=$bridgeProof
 & (Join-Path $tools 'LegacyValidation/LegacyBridgeValidation.exe') $LegacyExecutablePath $BridgeExecutablePath $NativeExecutablePath
 if($LASTEXITCODE -ne 0){throw 'Genuine pinned old product validator did not accept canonical bridge/reject raw native.'}
 foreach($key in @('Software\PCL','Software\PCLDebug','Software\PCL Living Fork')){if(Existing-Key $key){Pending ('Existing product store: HKCU\'+$key)}}
 $appData=[Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
 if([string]::IsNullOrEmpty($appData) -or -not [IO.Path]::IsPathRooted($appData)){Pending 'Actual Windows AppData is unavailable.'}
 foreach($directory in @((Join-Path $appData 'PCL'),(Join-Path $appData '.minecraft'))){if([IO.Directory]::Exists($directory) -or [IO.File]::Exists($directory)){Pending ('Existing user/game path: '+$directory)}}
 $osDrive=@([Environment]::GetLogicalDrives()|Where-Object {[IO.Directory]::Exists($_)})[0];$globalTaskTemp=Join-Path $osDrive 'ProgramData/PCL/TaskTemp';if([IO.Directory]::Exists($globalTaskTemp) -or [IO.File]::Exists($globalTaskTemp)){Pending 'Original startup clears global ProgramData/PCL/TaskTemp; require a pristine disposable machine.'}
 foreach($name in @('PCL_SingletonMutex','LivingPCL_SingletonMutex')){if(Existing-Mutex $name){Pending ('Pre-existing launcher lock '+$name)}}
 foreach($keyName in @('Software\PCL','Software\PCLDebug','Software\PCL Living Fork')){$key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyName);try{if($key.ValueCount -ne 0 -or $key.SubKeyCount -ne 0){Pending 'Product store appeared concurrently.'};$key.SetValue($marker,$id,[Microsoft.Win32.RegistryValueKind]::String);$ownedRegistry.Add($keyName)}finally{$key.Dispose()}}
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCLDebug',$true);try{$key.SetValue('SystemEulaVersion','2',[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
 $ownedAppData=Join-Path $appData 'PCL';[IO.Directory]::CreateDirectory($ownedAppData)|Out-Null;[IO.File]::WriteAllText((Join-Path $ownedAppData $marker),$id)
 Add-Type -Path (Join-Path $SmokeSupportRoot 'NativeSmokeWin32.cs') -ReferencedAssemblies System.Drawing.dll
 Add-Type -Path (Join-Path $SmokeSupportRoot 'NativeSmokeFileRelease.cs')
 & (Join-Path $PSScriptRoot 'native-upgrade/Test-StageFileRelease.ps1') -UpgradeScriptPath $PSCommandPath -SmokeSupportRoot $SmokeSupportRoot
 Add-Type -Path (Join-Path $PSScriptRoot 'native-upgrade/NativeUpdateWin32.cs')
 # Exercise the exact tracking function with harmless owned exit0/23 children
 # under this real Windows PowerShell before starting the original protocol.
 & (Join-Path $PSScriptRoot 'native-upgrade/Test-TrackedProcess.ps1') -TrackingScriptPath $PSCommandPath -EvidenceRoot (Join-Path $fixture 'tracked-process-regression') -SmokeSupportRoot $SmokeSupportRoot
 $oldNotice=$legacyProof.details.displayName+' '+$legacyProof.details.versionBaseName+' 版本说明';$newNotice=Get-ReviewedNativeStartupNotice $nativeProof
 foreach($case in $Cases){
  if($case -eq 'invalid-inputs'){
   New-Install $case $LegacyExecutablePath;$stage=Join-Path $script:install 'PCL/Updates/Canonical Managed Bridge.exe';Copy-Item -LiteralPath $BridgeExecutablePath -Destination $stage
   $script:caseRecord.Rejected=@()
   foreach($invalid in @(@{Name='wrapper-hash';Target=[IO.Path]::GetFileName($script:target);Hash=('0'*64)},@{Name='target-traversal';Target='../foreign.exe';Hash=$bridgeProof.sha256},@{Name='parent-target-mismatch';Target=[IO.Path]::GetFileName($script:target);Hash=$bridgeProof.sha256})){
    $bridge=Start-Owned $stage ('--livingpcl-update '+$PID+' "'+$invalid.Target+'" '+$invalid.Hash)
    if(-not $bridge.WaitForExit(10000) -or $bridge.ExitCode -ne 1 -or (Hash-File $script:target) -cne $script:caseRecord.OriginalSha256){throw ('Real managed bridge did not reject '+$invalid.Name+' without target mutation.')}
    if(@([IO.Directory]::EnumerateFiles((Join-Path $script:install 'PCL/Updates'),'pcl-fork-*.exe')).Count -ne 0){throw 'Invalid managed handoff extracted/launched a native payload.'};$script:caseRecord.Rejected+=,$invalid.Name
   };Check-Mutex $false
  }elseif($case -eq 'manual'){
   New-Install $case $BridgeExecutablePath;$timer=[Diagnostics.Stopwatch]::StartNew();$bridge=Start-Owned $script:target;if($bridge.WaitForExit(500)){throw 'Manual canonical bridge violated old 500ms stability.'};$helper=Wait-Helper $bridge $bridge.Id
   if(-not $bridge.WaitForExit(10000) -or $bridge.ExitCode -ne 0 -or $timer.ElapsedMilliseconds -lt 950){throw 'Manual bridge did not preserve one-second self-exit contract.'};$script:caseRecord.BridgeExitMilliseconds=$timer.ElapsedMilliseconds
   $restarted=Wait-Restart;$null=Wait-Ready $restarted $newNotice;if(-not $helper.WaitForExit(15000) -or $helper.ExitCode -ne 0){throw 'Actual native manual helper did not finish.'};if((Hash-File $script:target) -cne $nativeProof.sha256){throw 'Manual managed bridge did not become actual native bytes.'};Close-Main $restarted $newNotice
  }else{
   $source=$LegacyExecutablePath;$notice=$oldNotice;if($case -eq 'native-reinstall'){$source=$NativeExecutablePath;$notice=$newNotice}
   New-Install $case $source;$parent=Start-Owned $script:target;$null=Wait-Ready $parent $notice;$script:caseRecord.ParentId=$parent.Id
   if($case -eq 'restart-rollback'){
    Close-Main $parent $notice;$aclTarget=$script:target;$originalAcl=Get-Acl -LiteralPath $aclTarget;$deny=[Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.AccessControl.FileSystemRights]::ExecuteFile,[Security.AccessControl.AccessControlType]::Deny);$acl=Get-Acl -LiteralPath $aclTarget;$acl.AddAccessRule($deny);Set-Acl -LiteralPath $aclTarget -AclObject $acl
    $blocked=$false;try{$unexpected=Start-Owned $script:target;if($null -ne $unexpected){Pending 'Owned deny-execute ACL did not block actual launch; do not infer rollback.'}}catch [ComponentModel.Win32Exception]{$blocked=$_.Exception.NativeErrorCode -eq 5};if(-not $blocked){Pending 'Cannot prove precise owned-target execute denial.'}
    $prefix=New-Object byte[] 4096;$stream=[IO.File]::OpenRead($NativeExecutablePath);try{if($stream.Read($prefix,0,4096) -ne 4096){throw 'Native prefix truncated.'}}finally{$stream.Dispose()};$oldPrefix=New-Object byte[] 4096;$oldStream=[IO.File]::OpenRead($script:target);try{if($oldStream.Read($oldPrefix,0,4096) -ne 4096){throw 'Old target prefix truncated.'}}finally{$oldStream.Dispose()};if([Convert]::ToBase64String($prefix) -ceq [Convert]::ToBase64String($oldPrefix)){Pending 'Old/native first-block identity is indistinguishable; cannot prove installed native phase.'};$probe=[PclLfNativeUpgrade.RollbackProbe]::new($script:target,$prefix)
    $stage=Join-Path $script:install 'PCL/Updates/Actual Native Rollback.exe';Copy-Item -LiteralPath $NativeExecutablePath -Destination $stage;$helper=Start-Owned $stage ('--livingpcl-update '+$parent.Id+' "'+[IO.Path]::GetFileName($script:target)+'" '+$nativeProof.sha256)
    $script:caseRecord.Helper=@{ProcessId=$helper.Id;Path=$stage;Sha256=$nativeProof.sha256;Mode='Actual native four-argument helper after actual old Main normal close'}
    Wait-Failure $helper '';if(-not $probe.NativeTargetObserved -or -not $probe.BackupObserved){Pending 'Failure observed but native installation/backup phase was not captured; rollback cannot be claimed.'};if((Hash-File $script:target) -cne $script:caseRecord.OriginalSha256){throw 'Real failed restart did not restore original bytes.'};$script:caseRecord.PostReplaceNativeObserved=$probe.NativeTargetObserved;$script:caseRecord.BackupObserved=$probe.BackupObserved;$probe.Dispose();$probe=$null;Set-Acl -LiteralPath $aclTarget -AclObject $originalAcl;$aclTarget=$null;$originalAcl=$null
   }else{
    $stage=Join-Path $script:install 'PCL/Updates/Canonical Managed Bridge.exe';Copy-Item -LiteralPath $BridgeExecutablePath -Destination $stage;$bridge=Start-Owned $stage ('--livingpcl-update '+$parent.Id+' "'+[IO.Path]::GetFileName($script:target)+'" '+$bridgeProof.sha256)
    if($bridge.WaitForExit(500)){throw 'Canonical bridge violated original 500ms helper stability.'};$helper=Wait-Helper $bridge $parent.Id
    if(@([PclLfNativeSmoke.Win32]::Windows($helper.Id)).Count -gt 0){throw 'Native update helper unexpectedly ran normal UI before parent exit.'}
    if($case -eq 'parent-timeout'){
     Wait-Failure $helper '原启动器尚未退出。';if($script:caseRecord.Failure.ElapsedMilliseconds -lt 18000){Pending 'Observer cannot establish original twenty-second parent wait; preserve timing evidence.'};if($parent.HasExited -or (Hash-File $script:target) -cne $script:caseRecord.OriginalSha256){throw 'Parent timeout modified installed target or closed actual parent.'};if(-not $bridge.WaitForExit(10000) -or $bridge.ExitCode -ne 1){throw 'Bridge failed to report unchanged-target native failure.'};Close-Main $parent $notice
    }else{
     Close-Main $parent $notice;$restarted=Wait-Restart;$null=Wait-Ready $restarted $newNotice
     # Preserve the original short-circuit waits: helper 15s/zero first, then
     # bridge 15s/zero. Failure snapshots never add an unattempted bridge wait.
     $helperFinished=$helper.WaitForExit(15000);$helperExitCode=$null;if($helperFinished){$helperExitCode=$helper.ExitCode}
     $bridgeWaitAttempted=$false;$bridgeFinished=$null;$bridgeExitCode=$null
     if($helperFinished -and $helperExitCode -eq 0){$bridgeWaitAttempted=$true;$bridgeFinished=$bridge.WaitForExit(15000);if($bridgeFinished){$bridgeExitCode=$bridge.ExitCode}}
     $handoff=[ordered]@{Helper=@{ProcessId=$helper.Id;WaitAttempted=$true;WaitForExitMilliseconds=15000;WaitForExit=$helperFinished;ExitCode=$helperExitCode};Bridge=@{ProcessId=$bridge.Id;WaitAttempted=$bridgeWaitAttempted;WaitForExitMilliseconds=15000;WaitForExit=$bridgeFinished;ExitCode=$bridgeExitCode};RestartProcessId=$restarted.Id;Target=$script:target;ExpectedTargetSha256=$nativeProof.sha256}
     $script:caseRecord.Handoff=$handoff
     if(-not $helperFinished -or $helperExitCode -ne 0 -or -not $bridgeFinished -or $bridgeExitCode -ne 0){
      # Read only owned process/window/file state before forced cleanup. No
      # button is pressed and diagnostic errors cannot turn failure into pass.
      $handoff.Processes=@();$handoff.DiagnosticErrors=@()
      foreach($owned in @(@{Role='helper';Process=$helper},@{Role='bridge';Process=$bridge},@{Role='restart';Process=$restarted})){
       $observation=[ordered]@{Role=$owned.Role;ProcessId=$owned.Process.Id;HasExited=$null;ExitCode=$null;Windows=@()}
       try{$owned.Process.Refresh();$observation.HasExited=$owned.Process.HasExited;if($observation.HasExited){$observation.ExitCode=$owned.Process.ExitCode}}catch{$handoff.DiagnosticErrors+=($owned.Role+' process: '+$_.Exception.Message)}
       try{foreach($window in @([PclLfNativeSmoke.Win32]::Windows($owned.Process.Id))){$body=$null;if($window.ClassName -ceq '#32770'){$body=[PclLfNativeUpgrade.Dialogs]::Body($window.Handle)};$observation.Windows+=,@{Handle=$window.Handle.ToInt64();Title=$window.Title;ClassName=$window.ClassName;Body=$body}}}catch{$handoff.DiagnosticErrors+=($owned.Role+' windows: '+$_.Exception.Message)}
       $handoff.Processes+=,$observation
      }
      try{$handoff.TargetExists=[IO.File]::Exists($script:target);if($handoff.TargetExists){$handoff.TargetBytes=([IO.FileInfo]$script:target).Length;$handoff.TargetSha256=Hash-File $script:target};$handoff.ReplacementFiles=@([IO.Directory]::EnumerateFiles($script:install,([IO.Path]::GetFileName($script:target)+'.updating-*')))+@([IO.Directory]::EnumerateFiles($script:install,([IO.Path]::GetFileName($script:target)+'.update-backup-*')))}catch{$handoff.DiagnosticErrors+=('target: '+$_.Exception.Message)}
      try{Write-Output ('Actual upgrade helper/bridge diagnostic: '+($handoff|ConvertTo-Json -Depth 9 -Compress))}catch{$handoff.DiagnosticErrors+=('diagnostic output: '+$_.Exception.Message)}
      throw 'Actual helper/bridge did not finish native replacement successfully.'
     }
     if((Hash-File $script:target) -cne $nativeProof.sha256){throw 'Actual installed native payload SHA mismatch.'};Close-Main $restarted $newNotice
     if($case -eq 'native-reinstall'){$script:caseRecord.Limit='Same-build native reinstall proves native target/protocol identity, not a different-version upgrade.'}
    }
   }
  }
  Check-Install
  # Original bridge intentionally retains extracted pcl-fork-GUID.exe. Check
  # release of its handle, not deletion; do not invent a cleanup guarantee.
  if($script:caseRecord.Helper){Check-InstallFileRelease -Path $script:caseRecord.Helper.Path -Role 'extracted-helper'}
  $stageIndex=0;foreach($stageFile in [IO.Directory]::EnumerateFiles((Join-Path $script:install 'PCL/Updates'),'*.exe')){Check-InstallFileRelease -Path $stageFile -Role ('staged-'+$stageIndex);$stageIndex++}
  Copy-Item -LiteralPath $script:log -Destination (Join-Path $fixture ($case+'-last.log')) -ErrorAction SilentlyContinue
  $script:caseRecord.FinalSha256=Hash-File $script:target;$script:caseRecord.Result='passed'
 }
 $evidence.Result='passed';$evidence.Limit='Only explicitly selected real-entry protocol cases. No update was accepted or downloaded through product UI. Unknown dialogs fail/pending. No claim for absent cases, Windows execution elsewhere, distinct-version native upgrade, or full interaction/compatibility regression.'
}catch{if($_.Exception.Message -notlike 'PENDING:*'){$evidence.Result='failed'};$evidence.Error=$_.Exception.Message;throw}
finally{
 if($null -ne $probe){$probe.Dispose()}
 if($null -ne $aclTarget -and $null -ne $originalAcl){try{Set-Acl -LiteralPath $aclTarget -AclObject $originalAcl}catch{$evidence.CleanupErrors+=('Owned target ACL restore: '+$_.Exception.Message)}}
 try{foreach($row in @(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)})){if(-not @($ownedProcesses|Where-Object {$_.Id -eq $row.ProcessId}).Count){$ownedProcesses.Add([Diagnostics.Process]::GetProcessById($row.ProcessId))}}}catch{$allStopped=$false;$evidence.CleanupErrors+=('Owned process reconciliation: '+$_.Exception.Message)}
 foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$evidence.ForcedCleanup=$true;$process.Kill();[void]$process.WaitForExit(5000)};if(-not $process.HasExited){$allStopped=$false}}catch{$allStopped=$false;$evidence.CleanupErrors+=('Owned process cleanup: '+$_.Exception.Message)}finally{$process.Dispose()}}
 if($allStopped){foreach($keyName in $ownedRegistry){try{$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyName,$false);$matches=$false;try{$matches=$null -ne $key -and $key.GetValue($marker) -ceq $id}finally{if($null -ne $key){$key.Dispose()}};if($matches){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($keyName)}else{$evidence.CleanupErrors+=('Registry marker changed: '+$keyName)}}catch{$evidence.CleanupErrors+=('Registry cleanup: '+$_.Exception.Message)}};if($null -ne $ownedAppData){try{if([IO.File]::ReadAllText((Join-Path $ownedAppData $marker)) -ceq $id){[IO.Directory]::Delete($ownedAppData,$true)}else{$evidence.CleanupErrors+='AppData marker changed.'}}catch{$evidence.CleanupErrors+=('AppData cleanup: '+$_.Exception.Message)}}}else{$evidence.CleanupErrors+='Profile retained because owned process exit was not confirmed.'}
 if($evidence.ForcedCleanup -or $evidence.CleanupErrors.Count -gt 0){if($evidence.Result -eq 'passed'){$evidence.Result='failed'}}
 if([IO.Directory]::Exists($fixture)){[IO.File]::WriteAllText((Join-Path $fixture 'evidence.json'),($evidence|ConvertTo-Json -Depth 14),[Text.UTF8Encoding]::new($false));Write-Output ('Native upgrade evidence: '+(Join-Path $fixture 'evidence.json'))}
}
if($evidence.Result -ne 'passed'){throw ('Native upgrade '+$evidence.Result+'; see evidence.json')}
Write-Output ('PASS actual entry protocol cases: '+($Cases -join ', '))
