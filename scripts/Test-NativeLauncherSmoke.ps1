#requires -Version 5.1
param(
 [Parameter(Mandatory=$true)][string]$ExecutablePath,
 [string]$WorkspaceRoot=$env:GITHUB_WORKSPACE,
 [string]$NativeProjectPath,
 [string]$EvidenceRoot,
 [string]$DotNetPath='dotnet',
 [string]$FixtureProjectPath,
 [string]$NativeUpdaterSource,
 [ValidateRange(10,120)][int]$StartupTimeoutSeconds=60,
 [ValidateRange(3,30)][int]$CloseTimeoutSeconds=15,
 [switch]$ExerciseInteraction,
 [string]$InteractionProbePath,
 [ValidateRange(85,120)][int]$InteractionTimeoutSeconds=90
)
# CI-only, actual native Main entry. The observer never loads Main/App/WPF;
# only the metadata helper opt-in crypto leaf generates synthetic account data.
# No Startup bypass, update acceptance, dialog clicking, real credential, UAC or
# external release action. Requires fresh disposable Windows CI profile/machine.
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
if($env:OS -ne 'Windows_NT' -or $env:CI -ne 'true' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'PENDING: requires an isolated disposable GitHub-hosted Windows CI machine/profile.'}
if([IntPtr]::Size -ne 8 -or $PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'PENDING: invoke 64-bit Windows PowerShell -NoProfile -STA; no product runtime is hosted by PowerShell.'}
. (Join-Path $PSScriptRoot 'native-upgrade/ReviewedUpgradeNotices.ps1')
& (Join-Path $PSScriptRoot 'tests/Test-NativeSmokeReadiness.ps1') -SmokeScript $PSCommandPath
if(-not $WorkspaceRoot){throw 'PENDING: WorkspaceRoot must identify the dedicated CI workspace.'}
$WorkspaceRoot=[IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
if(-not [IO.Directory]::Exists($WorkspaceRoot)){throw 'WorkspaceRoot does not exist.'}
if([string]::Equals($WorkspaceRoot,[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -or $WorkspaceRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $WorkspaceRoot.IndexOf('AppData\Local\Temp\',[StringComparison]::OrdinalIgnoreCase) -ge 0){throw 'PENDING: original LF refuses TEMP installs; use the CI workspace without altering that protection.'}
if($WorkspaceRoot -match '[^\x00-\x7f]'){throw 'PENDING: this bounded fixture requires an ASCII CI workspace so original PurePath remains in its own install directory.'}
if(-not $NativeProjectPath){$NativeProjectPath=Join-Path $repoRoot 'modern/Main/PCL-Living-Fork.Modern.csproj'}
if(-not [IO.File]::Exists($NativeProjectPath)){throw 'The formal complete Native Main project is missing.'}
if(-not $FixtureProjectPath){$FixtureProjectPath=Join-Path $PSScriptRoot 'native-smoke/NativeSmokeFixture.csproj'}
if(-not $NativeUpdaterSource){$NativeUpdaterSource=Join-Path (Split-Path $NativeProjectPath -Parent) 'Modules/ModernBundleIdentity.cs'}
if(-not $EvidenceRoot){$EvidenceRoot=Join-Path $WorkspaceRoot 'artifacts/native-smoke'}
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
if(-not $EvidenceRoot.StartsWith($WorkspaceRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'EvidenceRoot must stay inside the dedicated CI workspace.'}
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$id=[Guid]::NewGuid().ToString('N');$fixture=Join-Path $EvidenceRoot ('NativeSmoke-'+$id)
$install=Join-Path $fixture 'Install with spaces';$differentCwd=Join-Path $fixture 'Different cwd';$cache=Join-Path $fixture 'Cache'
$installed=Join-Path $install 'LivingPCL Native Smoke.exe';$log=Join-Path $install 'PCL/Log1.txt';$marker='__PclLfNativeSmokeOwner'
$ownedRegistry=[Collections.Generic.List[string]]::new();$ownedAppData=$null;$nativeProcess=$null;$ownProcessStopped=$true
$accountSentinels=@();$startupSeed='native-smoke-seed-'+$id;$startupMutexObserver=$null
if($ExerciseInteraction){$script:interactionObserver=$null;$script:interactionObserverStopped=$true}
$evidence=[ordered]@{Result='pending';Scope='Actual product native cold start/WM_CLOSE/reopen, not complete functional regression';FixtureId=$id;InputExe=$ExecutablePath;MainProject=[IO.Path]::GetFullPath($NativeProjectPath);Runs=@();ForcedCleanup=$false;CleanupErrors=@();UserInteractive=[Environment]::UserInteractive;ObserverSessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId}
$evidence.StartupContracts=[ordered]@{ImportOrdering='pending';V2Preservation='pending';Accounts=[ordered]@{Count=0;CheckedPhases=@();CryptoSubject='Exact embedded Core/Shared only; no Main/App/WPF is loaded by the generator.'}}
function Pending([string]$reason){$evidence.Result='pending';throw ('PENDING: '+$reason)}
function Invoke-OwnedInteraction([int]$pass,[object]$window){
 if(-not $InteractionProbePath -or -not [IO.File]::Exists($InteractionProbePath)){Pending 'Opt-in interaction requires its separately built external UIAutomation probe.'}
 $arguments=@([IO.Path]::GetFullPath($InteractionProbePath),'--pid',[string]$nativeProcess.Id,'--hwnd',[string]$window.Handle.ToInt64(),'--fixture',$fixture,'--owner',$id,'--exe',$installed,'--pass',[string]$pass,'--sha256',$proof.sha256,'--workspace',$WorkspaceRoot,'--notice',$expectedNotice,'--notice-style',$proof.startupNoticeStyle,'--notice-source',$proof.startupNoticeSource)
 $quoted=@($arguments|ForEach-Object {if($_.Contains('"') -or $_.Contains("`r") -or $_.Contains("`n") -or $_.EndsWith('\')){throw 'Unsupported interaction argument quoting.'};'"'+$_+'"'})
 $start=[Diagnostics.ProcessStartInfo]::new($DotNetPath);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$fixture;$start.Arguments=$quoted -join ' '
 try{
  $script:interactionObserver=[Diagnostics.Process]::Start($start);if($null -eq $script:interactionObserver){throw 'External owned-window interaction observer did not start.'}
  $script:interactionObserverStopped=$false
  if(-not $script:interactionObserver.WaitForExit($InteractionTimeoutSeconds*1000)){Pending 'External UIAutomation observer exceeded its bound; cleanup must prove helper exit, and timeout is not an interaction pass.'}
  $script:interactionObserver.Refresh()
  if(-not $script:interactionObserver.HasExited){throw 'Interaction observer wait returned without a final exit proof.'}
  $script:interactionObserverStopped=$true
  $file=Join-Path $fixture ('interaction-'+$pass+'.json')
  if(-not [IO.File]::Exists($file)){throw 'Interaction observer did not produce owned evidence.'}
  $result=[IO.File]::ReadAllText($file)|ConvertFrom-Json
  $run.Interaction=$result
  if($result.FixtureId -cne $id -or $result.Pass -cne [string]$pass -or $result.ProcessId -ne $nativeProcess.Id -or $result.MainHandle -ne $window.Handle.ToInt64() -or $result.MainSha256 -cne $proof.sha256){throw 'Interaction evidence is not bound to this exact real Main pass.'}
  if($script:interactionObserver.ExitCode -ne 0 -or $result.Result -cne 'passed'){if($result.Result -ceq 'pending'){Pending ('Owned interaction: '+$result.Error)};throw ('Owned interaction failed: '+$result.Error)}
  Assert-OwnedProcessImage ([PclLfNativeSmoke.Win32]::ObserveProcessImage($nativeProcess,$false)) $installed
  Check-Log (Read-OwnedLog) $expectedNotice
 }finally{
  if($null -ne $script:interactionObserver){
   if(-not $script:interactionObserverStopped){
    try{
     $script:interactionObserver.Refresh()
     if($script:interactionObserver.HasExited){$script:interactionObserverStopped=$true}
     else{
      $script:interactionObserver.Kill()
      if($script:interactionObserver.WaitForExit(5000)){$script:interactionObserver.Refresh();$script:interactionObserverStopped=$script:interactionObserver.HasExited}
     }
    }catch{
     $evidence.CleanupErrors+=('Owned interaction observer stop: '+$_.Exception.Message)
     # Kill may race an actual exit. Only a fresh positive HasExited receipt
     # can authorize cleanup; an exception or an elapsed wait cannot do so.
     try{$script:interactionObserver.Refresh();if($script:interactionObserver.HasExited){$script:interactionObserverStopped=$true}}catch{$evidence.CleanupErrors+=('Owned interaction observer exit query: '+$_.Exception.Message)}
    }
   }
   if($script:interactionObserverStopped){$script:interactionObserver.Dispose();$script:interactionObserver=$null}
   else{
    $evidence.Result='failed'
    $evidence.InteractionObserverStop=[ordered]@{ExitProved=$false;ObserverProcessId=$script:interactionObserver.Id;MainProcessId=$nativeProcess.Id;Retained='Observer handle, actual Main process/handle, startup locks and owned profile are retained; no Main close, Dispose or profile deletion is authorized.'}
    $evidence.CleanupErrors+='Owned interaction observer exit remains unknown; preserve every dependent Main/profile/lock context.'
   }
  }
 }
}
function Existing-Key([string]$name){foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)){$base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view);try{$node=$base.OpenSubKey($name,$false);if($null -ne $node){$node.Dispose();return $true}}finally{$base.Dispose()}};return $false}
function Existing-Mutex([string]$name){try{$gate=[Threading.Mutex]::OpenExisting($name);$gate.Dispose();return $true}catch [Threading.WaitHandleCannotBeOpenedException]{return $false}}
function Check-Mutex([string]$name,[bool]$expectHeld){
 $gate=$null;$acquired=$false
 try{try{$gate=[Threading.Mutex]::OpenExisting($name)}catch [Threading.WaitHandleCannotBeOpenedException]{if($expectHeld){throw ('Actual Main did not create '+$name)};return}
  try{$acquired=$gate.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true;if($expectHeld){throw 'Actual running Main abandoned its session lock.'}}
  if($expectHeld -eq $acquired){throw ('Unexpected actual Main session lock state: '+$name)}
 }finally{if($null -ne $gate){if($acquired){$gate.ReleaseMutex()};$gate.Dispose()}}
}
function Hash-OwnedFile([string]$fileName){$stream=[IO.File]::Open($fileName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}}
function Read-OwnedLog {if(-not [IO.File]::Exists($log)){return ''};$reader=[IO.StreamReader]::new([IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)));try{return $reader.ReadToEnd()}finally{$reader.Dispose()}}
function Check-Log([string]$text,[string]$expectedNotice,[switch]$RequireComplete){
 $complete=Test-ReviewedStartupNotice -Text $text -Notice $expectedNotice -NativeVersion $proof.versionBaseName -NativePhase -NativeStyle $proof.startupNoticeStyle -NativeSource $proof.startupNoticeSource
 if($RequireComplete -and -not $complete){throw 'Actual Main final close has no complete reviewed startup notice.'}
}
function Assert-OwnedProcessImage([object]$image,[string]$expectedPath){
 if(-not $image.QuerySucceeded){throw ('QueryFullProcessImageNameW failed for the actual process handle: Win32='+$image.Win32Error+'; '+$image.QueryErrorType+'; '+$image.QueryError)}
 if([string]::IsNullOrEmpty($image.Path) -or -not [string]::Equals($image.Path,$expectedPath,[StringComparison]::OrdinalIgnoreCase)){throw ('Actual process image differs from owned target. Expected='+$expectedPath+'; QueryFullProcessImageNameW='+$image.Path+'; MainModule(diagnostic only)='+$image.LegacyMainModulePath)}
}
function Assert-LfNormalClose([int]$exitCode,[string]$text,[string]$expectedNotice){
 # LF50 EndProgramForce(Success=0) flushes and self-Kill()s. The fixed Windows
 # runtime10.0.14 Process.Kill calls TerminateProcess(handle,-1), not Exit(0).
 if($exitCode -ne -1){throw ('Actual Main did not preserve LF Windows self-Kill exit -1; observed '+$exitCode)}
 Check-Log $text $expectedNotice -RequireComplete
 $closing=[regex]::Match($text,'(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I [^\r\n]*\] 收到关闭指令\r?$')
 $success=[regex]::Match($text,'(?m)^\d{2}:\d{2}:\d{2}\.\d{3} I [^\r\n]*\] 程序已退出，返回值：Success\r?$')
 if(-not $closing.Success -or -not $success.Success -or $success.Index -le $closing.Index){throw 'Actual Main lacks ordered real LF closing and Success termination log receipts.'}
}
function Write-OwnedCloseDiagnostic([object]$exitCode,[string]$text,[string]$phase){
 # Only this GUID fixture's synthetic product log. Cap console/file evidence;
 # no AppData/account, global event log, desktop or unrelated process is read.
 $lines=@($text.Replace("`r",'').Split("`n")|Select-Object -Last 25|ForEach-Object {if($_.Length -gt 512){$_.Substring($_.Length-512)}else{$_}})
 $tail=$lines -join "`n";if($tail.Length -gt 12000){$tail=$tail.Substring($tail.Length-12000)}
 $hex=$null;if($null -ne $exitCode){$hex='0x'+([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode),0)).ToString('X8')}
 $diagnostic=[ordered]@{Phase=$phase;ObservedExitCode=$exitCode;ObservedExitCodeHex=$hex;ExpectedWindowsLfExitCode=-1;HasClosingReceipt=$text.Contains('收到关闭指令');HasSuccessReceipt=$text.Contains('程序已退出，返回值：Success');LogCharacters=$text.Length;BoundedOwnedLogTail=$tail;ForcedCleanup=$evidence.ForcedCleanup}
 [IO.File]::WriteAllText((Join-Path $fixture ('process-'+$phase+'-diagnostics.json')),($diagnostic|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
 Write-Output ('Actual Native Main bounded diagnostic: '+($diagnostic|ConvertTo-Json -Depth 4 -Compress))
}

function Assert-SyntheticPreferenceImport([bool]$expected,[string]$phase){
 $official=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCL',$false);$legacy=$null;$dedicated=$null
 try{
  $legacy=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCLDebug',$false)
  if($null -eq $official -or $null -eq $legacy -or $official.GetValue($marker,$null) -cne $id -or $legacy.GetValue($marker,$null) -cne $id){throw 'Synthetic startup preference namespaces are not owned by this fixture.'}
  if($legacy.GetValue('ToolDownloadThread',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne '9' -or $legacy.GetValueKind('ToolDownloadThread') -ne [Microsoft.Win32.RegistryValueKind]::String){throw 'Synthetic old preference source changed during actual entry.'}
  $dedicated=Open-OwnedRegistry 'Software\PCL Living Fork' $false
  if($null -ne $legacy.GetValue('LivingPclSharedPreferencesMigrated',$null)){throw 'LF migration marker remained in the legacy shared namespace.'}
  $migrated=$dedicated.GetValue('LivingPclSharedPreferencesMigrated',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  $current=$official.GetValue('ToolDownloadThread',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  if($expected){if($migrated -cne 'True' -or $current -cne '9' -or $dedicated.GetValueKind('LivingPclSharedPreferencesMigrated') -ne [Microsoft.Win32.RegistryValueKind]::String -or $official.GetValueKind('ToolDownloadThread') -ne [Microsoft.Win32.RegistryValueKind]::String){throw ('Actual normal Main did not demonstrate that the preference fixture can observe import: '+$phase)}}
  elseif($null -ne $migrated -or $null -ne $current){throw ('Actual --update utility imported old shared preferences: '+$phase)}
 }finally{if($null -ne $dedicated){$dedicated.Dispose()};if($null -ne $legacy){$legacy.Dispose()};if($null -ne $official){$official.Dispose()}}
}
function Test-SafeUtilityImportExclusion {
 # The real current Main rejects the original official --update option and
 # Environment.Exit(Fail=1); this never dispatches GPU/memory or the four-arg LF
 # replacement helper. Main/App is not loaded or invoked in this observer.
 $record=[ordered]@{Argument='--update';Result='pending';ExpectedExitCode=1;Scope='Only safe refusal of the retired official updater; GPU/memory utility paths remain unexecuted.'}
 $evidence.UtilityPreferenceImport=$record
 Assert-SyntheticPreferenceImport $false 'before-safe-utility'
 $info=[Diagnostics.ProcessStartInfo]::new($installed);$info.UseShellExecute=$false;$info.WorkingDirectory=$differentCwd;$info.Arguments='--update'
 $info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $fixture 'BundleExtract'
 $script:nativeProcess=[Diagnostics.Process]::Start($info);if($null -eq $script:nativeProcess){throw 'Actual safe utility entry did not start.'};$record.ProcessId=$script:nativeProcess.Id
 $image=[PclLfNativeSmoke.Win32]::ObserveProcessImage($script:nativeProcess,$false);$record.ImageIdentity=$image;Assert-OwnedProcessImage $image $installed
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not $script:nativeProcess.HasExited -and $watch.ElapsedMilliseconds -lt 15000){
  $windows=@([PclLfNativeSmoke.Win32]::Windows($script:nativeProcess.Id))
  if($windows.Count -ne 0){$evidence.Result='pending';throw ('Unexpected actual utility window; never auto-clicked: '+$windows[0].Title+' / '+$windows[0].ClassName)}
  Start-Sleep -Milliseconds 25;$script:nativeProcess.Refresh()
 }
 if(-not $script:nativeProcess.HasExited){throw 'Actual safe utility did not exit within the original bounded process observer.'}
 $record.ElapsedMilliseconds=$watch.ElapsedMilliseconds;$record.ExitCode=$script:nativeProcess.ExitCode
 if($record.ExitCode -ne 1){throw ('Actual official-update refusal lost its Fail=1 exit contract: '+$record.ExitCode)}
 Assert-SyntheticPreferenceImport $false 'after-safe-utility'
 $record.Result='passed';$script:nativeProcess.Dispose();$script:nativeProcess=$null
}

function Open-OwnedRegistry([string]$name,[bool]$writable){
 if(-not $ownedRegistry.Contains($name)){throw 'Not an owned startup Registry namespace.'}
 $node=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($name,$writable)
 if($null -eq $node){throw 'Owned startup Registry namespace disappeared.'}
 if($node.GetValue($marker,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $id){$node.Dispose();throw 'Owned startup Registry marker changed.'}
 return $node
}
function Assert-StartupAccounts([string]$phase,[bool]$migrated=$false){
 if($accountSentinels.Count -ne 13){throw 'All thirteen reviewed LF Microsoft sentinels are mandatory.'}
 $official=Open-OwnedRegistry 'Software\PCL' $false;$dedicated=$null
 try{
  $dedicated=Open-OwnedRegistry 'Software\PCL Living Fork' $false
  if($official.GetValue('Identify',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $startupSeed -or $official.GetValueKind('Identify') -ne [Microsoft.Win32.RegistryValueKind]::String){throw 'Synthetic identity seed changed during genuine startup.'}
  foreach($record in $accountSentinels){
   $source=$official.GetValue($record.physicalName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
   $target=$dedicated.GetValue($record.physicalName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
   $node=if($migrated){$dedicated}else{$official};$raw=if($migrated){$target}else{$source}
   if($raw -isnot [string] -or $raw -cne $record.raw -or $node.GetValueKind($record.physicalName) -ne [Microsoft.Win32.RegistryValueKind]::String){throw ('Genuine startup changed an authoritative LF account raw value/kind: '+$record.logicalName+' / '+$phase)}
   if(($migrated -and $null -ne $source) -or (-not $migrated -and $null -ne $target)){throw ('Startup account namespace migration ordering differs: '+$phase)}
  }
 }finally{if($null -ne $dedicated){$dedicated.Dispose()};$official.Dispose()}
 $evidence.StartupContracts.Accounts.CheckedPhases+=,$phase
}

function Test-MutexBeforeImport([object]$observer){
 $row=[ordered]@{Result='pending';ExpectedExitCode=2;OwnActivationHandle=$observer.Handle;OwnActivationCaption=$observer.Caption}
 $evidence.StartupContracts.MutexProbe=$row
 $before=$observer.ActivationMessages;Assert-SyntheticPreferenceImport $false 'before-mutex-probe';Assert-StartupAccounts 'before-mutex-probe'
 $info=[Diagnostics.ProcessStartInfo]::new($installed);$info.UseShellExecute=$false;$info.WorkingDirectory=$differentCwd
 $info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $fixture 'BundleExtract'
 $script:nativeProcess=[Diagnostics.Process]::Start($info);if($null -eq $script:nativeProcess){throw 'Genuine mutex-contender Main did not start.'};$row.ProcessId=$script:nativeProcess.Id
 $row.ImageIdentity=[PclLfNativeSmoke.Win32]::ObserveProcessImage($script:nativeProcess,$false);Assert-OwnedProcessImage $row.ImageIdentity $installed
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not $script:nativeProcess.HasExited -and $watch.ElapsedMilliseconds -lt 15000){
  $observer.PumpOwnMessages()
  $windows=@([PclLfNativeSmoke.Win32]::Windows($script:nativeProcess.Id))
  if($windows.Count -ne 0){$evidence.Result='pending';throw ('Unexpected actual competing Main window; never auto-clicked: '+$windows[0].Title+' / '+$windows[0].ClassName)}
  Start-Sleep -Milliseconds 25;$script:nativeProcess.Refresh()
 }
 $observer.PumpOwnMessages()
 if(-not $script:nativeProcess.HasExited){throw 'Genuine competing Main exceeded its bounded rejection observer.'}
 $row.ElapsedMilliseconds=$watch.ElapsedMilliseconds;$row.ExitCode=$script:nativeProcess.ExitCode;$row.ActivationMessages=$observer.ActivationMessages-$before
 if($row.ExitCode -ne 2 -or $row.ActivationMessages -ne 1){throw 'The actual blocked Main did not preserve one original 6402 activation and Exit2 before preferences could import.'}
 Assert-SyntheticPreferenceImport $false 'after-mutex-probe';Assert-StartupAccounts 'after-mutex-probe'
 $row.Result='passed';$script:nativeProcess.Dispose();$script:nativeProcess=$null
}

try{
 foreach($directory in @($fixture,$install,$differentCwd,$cache)){[IO.Directory]::CreateDirectory($directory)|Out-Null}
 [IO.File]::WriteAllText((Join-Path $fixture 'owner.txt'),$id)
 $tools=Join-Path $fixture 'Tools';$intermediate=Join-Path $fixture 'ToolObj'
 & $DotNetPath build $FixtureProjectPath -c Release --output $tools ('-p:BaseIntermediateOutputPath='+$intermediate+'\') ('-p:MSBuildProjectExtensionsPath='+$intermediate+'\') ('-p:LFNativeUpdaterSource='+[IO.Path]::GetFullPath($NativeUpdaterSource)) -warnaserror -v:q
 if($LASTEXITCODE -ne 0){throw 'Candidate metadata inspector failed to compile.'}
 & $DotNetPath (Join-Path $tools 'NativeSmokeFixture.dll') --self-test-notice-metadata
 if($LASTEXITCODE -ne 0){throw 'Metadata-only startup notice route regression failed.'}
 $proofJson=& $DotNetPath (Join-Path $tools 'NativeSmokeFixture.dll') $ExecutablePath
 if($LASTEXITCODE -ne 0){throw 'Not the genuine full native Main entry/BAML/resource/profile contract; no profile was created.'}
 $proof=($proofJson -join "`n")|ConvertFrom-Json;$evidence.MainProof=$proof
 if($proof.buildType -notin @(0,50)){Pending 'Debug/unknown builds may invoke original developer tests; smoke requires actual Release or Snapshot product.'}
 $expectedNotice=Get-ReviewedNativeStartupNotice $proof
 # Existence only: do not read any pre-existing settings/accounts/game files.
 foreach($keyName in @('Software\PCL','Software\PCLDebug','Software\PCL Living Fork')){if(Existing-Key $keyName){Pending ('Pre-existing user/product store: HKCU\'+$keyName)}}
 $appData=[Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
 if([string]::IsNullOrEmpty($appData) -or -not [IO.Path]::IsPathRooted($appData)){Pending 'Actual Windows profile AppData path is unavailable.'}
 foreach($directory in @((Join-Path $appData 'PCL'),(Join-Path $appData '.minecraft'))){if([IO.Directory]::Exists($directory) -or [IO.File]::Exists($directory)){Pending ('Pre-existing user/game path: '+$directory)}}
 $osDrive=@([Environment]::GetLogicalDrives()|Where-Object {[IO.Directory]::Exists($_)})[0]
 $globalTaskTemp=Join-Path $osDrive 'ProgramData/PCL/TaskTemp'
 if([IO.Directory]::Exists($globalTaskTemp) -or [IO.File]::Exists($globalTaskTemp)){Pending 'Original startup clears ProgramData/PCL/TaskTemp; this is not a pristine disposable machine.'}
 $mutexNames=@('PCL_SingletonMutex','LivingPCL_SingletonMutex')
 foreach($name in $mutexNames){if(Existing-Mutex $name){Pending ('Existing launcher lock: '+$name)}}
 foreach($keyName in @('Software\PCL','Software\PCLDebug','Software\PCL Living Fork')){
  $node=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($keyName)
  try{if($node.ValueCount -ne 0 -or $node.SubKeyCount -ne 0){Pending 'Profile store appeared concurrently; refusing to modify it.'};$node.SetValue($marker,$id,[Microsoft.Win32.RegistryValueKind]::String);$ownedRegistry.Add($keyName)}finally{$node.Dispose()}
 }
 $node=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCL',$true)
 try{$node.SetValue('Identify',$startupSeed,[Microsoft.Win32.RegistryValueKind]::String);$node.SetValue('SystemSystemCache',$cache+'\',[Microsoft.Win32.RegistryValueKind]::String);$node.SetValue('LaunchFolders','Smoke Fixture>'+[IO.Path]::Combine($install,'.minecraft')+'\',[Microsoft.Win32.RegistryValueKind]::String)}finally{$node.Dispose()}
 $node=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PCLDebug',$true)
 try{$node.SetValue('SystemEulaVersion','2',[Microsoft.Win32.RegistryValueKind]::String);$node.SetValue('ToolDownloadThread','9',[Microsoft.Win32.RegistryValueKind]::String)}finally{$node.Dispose()}
 # A synthetic already-accepted local test state, not a click accepting terms.
 # Only valid synthetic LF account ciphertext is seeded below; LoginType is Legacy.
 # No real account token/profile is read or used, and no authentication is requested.
 $ownedAppData=Join-Path $appData 'PCL';[IO.Directory]::CreateDirectory($ownedAppData)|Out-Null;[IO.File]::WriteAllText((Join-Path $ownedAppData $marker),$id)
 [IO.Directory]::CreateDirectory((Join-Path $install '.minecraft/versions'))|Out-Null
 [IO.Directory]::CreateDirectory((Join-Path $install 'PCL'))|Out-Null
 [IO.File]::WriteAllText((Join-Path $install 'PCL/Setup.ini'),('NativeSmokeSentinel:'+$id+"`r`nLaunchFolderSelect:"+(Join-Path $install '.minecraft')+"\`r`nUiLauncherLogo:False`r`nUiCustomType:0`r`nLivingModpackMode:False`r`nToolLauncherUpdateSource:0`r`nToolLauncherUpdateChannel:0`r`n"),[Text.UTF8Encoding]::new($false))
 $gameSentinel=Join-Path $install '.minecraft/NativeSmoke.sentinel';[IO.File]::WriteAllText($gameSentinel,$id)
 Copy-Item -LiteralPath $ExecutablePath -Destination $installed
 if((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.sha256){throw 'Installed fixture bytes differ from actual candidate preflight.'}
 Add-Type -Path (Join-Path $PSScriptRoot 'native-smoke/NativeSmokeWin32.cs') -ReferencedAssemblies System.Drawing.dll
 Add-Type -Path (Join-Path $PSScriptRoot 'native-smoke/NativeSmokeFileRelease.cs')
 Add-Type -Path (Join-Path $PSScriptRoot 'native-smoke/NativeSmokeStartupMutex.cs')
 $packetJson=& $DotNetPath (Join-Path $tools 'NativeSmokeFixture.dll') --startup-sentinels $ExecutablePath $id $startupSeed
 if($LASTEXITCODE -ne 0){throw 'Actual embedded Core could not generate valid synthetic startup ciphertext.'}
 $packet=($packetJson -join "`n")|ConvertFrom-Json
 if($packet.kind -cne 'PCLLF.NativeStartupSyntheticAccounts' -or $packet.schemaVersion -ne 1 -or $packet.fixtureId -cne $id -or $packet.seed -cne $startupSeed -or $packet.nativeSha256 -cne $proof.sha256 -or $packet.coreSha256 -cnotmatch '\A[0-9a-f]{64}\z' -or $packet.sharedSha256 -cnotmatch '\A[0-9a-f]{64}\z'){throw 'Startup synthetic packet is not bound to the exact genuine native Main and current fixture.'}
 $expectedNames=@('LoginMsJson','CacheMsOAuthRefresh','CacheMsAccess','CacheMsProfileJson','CacheMsUuid','CacheMsName','CacheMsV2Migrated','CacheMsV2OAuthRefresh','CacheMsV2Access','CacheMsV2ProfileJson','CacheMsV2Uuid','CacheMsV2Name','CacheMsV2Expires')
 $accountSentinels=@($packet.records)
 if($accountSentinels.Count -ne 13 -or @($accountSentinels|ForEach-Object {$_.logicalName}|Sort-Object -Unique).Count -ne 13 -or @($expectedNames|Where-Object {$_ -cnotin @($accountSentinels|ForEach-Object {$_.logicalName})}).Count -ne 0){throw 'Startup packet must contain exactly the thirteen reviewed LF logical names.'}
 $node=Open-OwnedRegistry 'Software\PCL' $true
 try{
  $node.SetValue('LoginType','0',[Microsoft.Win32.RegistryValueKind]::String)
  foreach($record in $accountSentinels){
   if($record.physicalName -cne ($record.logicalName+'_LF') -or $record.kind -cne 'String' -or $record.raw -isnot [string] -or $record.raw.Length -eq 0){throw 'Invalid startup account physical-name/kind/cipher contract.'}
   if($null -ne $node.GetValue($record.physicalName,$null)){throw 'An account record appeared concurrently in the owned namespace.'}
   $node.SetValue($record.physicalName,$record.raw,[Microsoft.Win32.RegistryValueKind]::String)
  }
 }finally{$node.Dispose()}
 $evidence.StartupContracts.Accounts.Count=13;$evidence.StartupContracts.Accounts.CoreSha256=$packet.coreSha256;$evidence.StartupContracts.Accounts.SharedSha256=$packet.sharedSha256
 Assert-StartupAccounts 'before-genuine-entry';Assert-SyntheticPreferenceImport $false 'before-genuine-entry'
 # On failure, keep these real locks/HWND until the existing outer cleanup
 # has proved the owned product process stopped. Never release a live context.
 $startupMutexObserver=[PclLfNativeSmoke.StartupMutexFixture]::new($id)
 Test-MutexBeforeImport $startupMutexObserver
 $evidence.StartupContracts.ImportOrdering='blocked-before-import'
 $beforeUtility=$startupMutexObserver.ActivationMessages
 Test-SafeUtilityImportExclusion
 $startupMutexObserver.PumpOwnMessages()
 if($startupMutexObserver.ActivationMessages -ne $beforeUtility){throw 'The safe utility incorrectly entered the normal single-instance path.'}
 Assert-StartupAccounts 'after-safe-utility'
 $startupMutexObserver.Dispose();$startupMutexObserver=$null
 foreach($name in $mutexNames){Check-Mutex $name $false}
 $previousLogHash=$null
 foreach($pass in @(1,2)){
  $run=[ordered]@{Pass=$pass;ActualExe=$installed;WorkingDirectory=$differentCwd;NormalExit=$false;ExpectedInformationNoticeVisible=$false};$evidence.Runs+=,$run
  $info=[Diagnostics.ProcessStartInfo]::new($installed);$info.UseShellExecute=$false;$info.WorkingDirectory=$differentCwd
  $info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $fixture 'BundleExtract'
  $nativeProcess=[Diagnostics.Process]::Start($info);if($null -eq $nativeProcess){throw 'Actual native Main did not start.'};$run.ProcessId=$nativeProcess.Id
  $watch=[Diagnostics.Stopwatch]::StartNew();$readyAt=-1;$mainWindow=$null
  while($watch.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds){
   $nativeProcess.Refresh();if($nativeProcess.HasExited){throw ('Actual Main exited before ready: '+$nativeProcess.ExitCode)}
   $firstImage=-not $run.Contains('ImageIdentityFirst')
   $image=[PclLfNativeSmoke.Win32]::ObserveProcessImage($nativeProcess,$firstImage)
   $run.ImageIdentityLast=$image
   if($firstImage){$run.ImageIdentityFirst=$image}
   if($firstImage -or -not $image.QuerySucceeded -or -not [string]::Equals($image.Path,$installed,[StringComparison]::OrdinalIgnoreCase)){
    $imageDiagnostic=[ordered]@{Source='QueryFullProcessImageNameW/process.Handle/flags0';ExpectedPath=$installed;ProcessId=$nativeProcess.Id;ElapsedMilliseconds=$watch.ElapsedMilliseconds;FirstSample=$run.ImageIdentityFirst;CurrentSample=$image}
    [IO.File]::WriteAllText((Join-Path $fixture ('process-image-'+$pass+'-diagnostics.json')),($imageDiagnostic|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    Write-Output ('Actual Native process image diagnostic: '+($imageDiagnostic|ConvertTo-Json -Depth 5 -Compress))
   }
   Assert-OwnedProcessImage $image $installed
   $windows=@([PclLfNativeSmoke.Win32]::Windows($nativeProcess.Id))
   foreach($window in $windows){if($window.Title.Length -gt 0 -and ($window.ClassName -notlike 'HwndWrapper*' -or $window.Title.Trim() -cne 'LivingPCL')){Pending ('Unexpected native product window: '+$window.Title+' / '+$window.ClassName)}}
   $mainWindow=@($windows|Where-Object {$_.Title.Trim() -ceq 'LivingPCL' -and $_.ClassName -like 'HwndWrapper*' -and $_.Right -gt $_.Left -and $_.Bottom -gt $_.Top})|Select-Object -First 1
   $text=Read-OwnedLog;Check-Log $text $expectedNotice
   $allStages=$text.Contains('第一阶段加载用时：') -and $text.Contains('第二阶段加载用时：') -and $text.Contains('第三阶段加载用时：') -and $text.Contains('PCL 位置：') -and $text.Contains($installed) -and (Test-ReviewedStartupNotice -Text $text -Notice $expectedNotice -NativeVersion $proof.versionBaseName -NativePhase -NativeStyle $proof.startupNoticeStyle -NativeSource $proof.startupNoticeSource)
   $fresh=$pass -eq 1 -or ([IO.File]::Exists($log) -and (Hash-OwnedFile $log) -cne $previousLogHash)
   if($null -ne $mainWindow -and $allStages -and $fresh){if($readyAt -lt 0){$readyAt=$watch.ElapsedMilliseconds};if($watch.ElapsedMilliseconds-$readyAt -ge 1500){break}}else{$readyAt=-1}
   Start-Sleep -Milliseconds 150
  }
  if($null -eq $mainWindow -or $readyAt -lt 0 -or $watch.ElapsedMilliseconds-$readyAt -lt 1500){Pending 'Actual Main/animation/log stages did not reach a stable visible checkpoint within the bound.'}
  $run.ReadyMilliseconds=$watch.ElapsedMilliseconds;$run.Window=@{Title=$mainWindow.Title;Class=$mainWindow.ClassName;Handle=$mainWindow.Handle.ToInt64();Left=$mainWindow.Left;Top=$mainWindow.Top;Right=$mainWindow.Right;Bottom=$mainWindow.Bottom}
  $run.ExpectedInformationNoticeVisible=Test-ReviewedStartupNotice -Text $text -Notice $expectedNotice -NativeVersion $proof.versionBaseName -NativePhase -NativeStyle $proof.startupNoticeStyle -NativeSource $proof.startupNoticeSource
  foreach($name in $mutexNames){Check-Mutex $name $true}
  Assert-SyntheticPreferenceImport $true ('normal-ready-'+$pass)
  Assert-StartupAccounts ('normal-ready-'+$pass) $true;$run.AuthoritativeAccountsPreservedAtReady=$true
  $run.SyntheticPreferenceImportObserved=$true
  if($ExerciseInteraction){Invoke-OwnedInteraction $pass $mainWindow;Assert-StartupAccounts ('after-interaction-'+$pass) $true}
  $run.Screenshot=[PclLfNativeSmoke.Win32]::CaptureOwnWindow($mainWindow.Handle,(Join-Path $fixture ('main-'+$pass+'.png')))
  if($pass -eq 2){$rotated=Join-Path $install 'PCL/Log2.txt';if(-not [IO.File]::Exists($rotated) -or (Hash-OwnedFile $rotated) -cne $previousLogHash){throw 'Reopen did not preserve original LF log rotation.'}}
  $closeWatch=[Diagnostics.Stopwatch]::StartNew();$closeBudget=$CloseTimeoutSeconds*1000
  if(-not [PclLfNativeSmoke.Win32]::PostClose($mainWindow.Handle)){throw 'Actual selected Main HWND did not accept WM_CLOSE.'}
  $closeRemaining=[Math]::Max(0,$closeBudget-[int]$closeWatch.ElapsedMilliseconds)
  if($closeRemaining -le 0 -or -not $nativeProcess.WaitForExit($closeRemaining)){throw 'Actual Main failed normal bounded close; forced cleanup is not success.'}
  $run.ExitCode=$nativeProcess.ExitCode
  $text=Read-OwnedLog;Write-OwnedCloseDiagnostic $run.ExitCode $text ('close-'+$pass)
  Assert-LfNormalClose $run.ExitCode $text $expectedNotice
  $run.NormalExit=$true;$nativeProcess.Dispose();$nativeProcess=$null
  foreach($name in $mutexNames){Check-Mutex $name $false}
  # Same WM_CLOSE deadline includes real exit and FileShare.None handle release.
  # Restart Manager samples only this GUID copied EXE, never shuts down owners.
  $release=[PclLfNativeSmoke.FileRelease]::WaitForExclusiveRelease($installed,$closeWatch,$closeBudget)
  $run.ExclusiveFileRelease=$release
  [IO.File]::WriteAllText((Join-Path $fixture ('file-release-'+$pass+'-diagnostics.json')),($release|ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
  Write-Output ('Actual Native exclusive file release: '+($release|ConvertTo-Json -Depth 7 -Compress))
  if(-not $release.Succeeded){throw ('Actual owned EXE failed exclusive ReadWrite/FileShare.None release within close deadline: '+$release.ErrorType+'; Win32='+$release.LastWin32Error+'; HResult='+$release.LastHResult+'; '+$release.Error)}
  Assert-StartupAccounts ('normal-close-'+$pass) $true;$run.AuthoritativeAccountsPreservedAfterClose=$true
  if([IO.File]::ReadAllText($gameSentinel) -cne $id -or -not [IO.File]::ReadAllText((Join-Path $install 'PCL/Setup.ini')).Contains('NativeSmokeSentinel:'+$id)){throw 'Actual startup/close lost fixture settings/game sentinel.'}
  $updateFolder=Join-Path $install 'PCL/Updates';if([IO.Directory]::Exists($updateFolder) -and @([IO.Directory]::EnumerateFiles($updateFolder,'*.exe',[IO.SearchOption]::AllDirectories)).Count -gt 0){throw 'An update payload appeared without any acceptance action.'}
  Copy-Item -LiteralPath $log -Destination (Join-Path $fixture ('startup-'+$pass+'.log'))
  $previousLogHash=Hash-OwnedFile $log
 }
 $evidence.StartupContracts.ImportOrdering='passed-blocked-then-normal-import';$evidence.StartupContracts.V2Preservation='passed-two-real-FormMain-startups'
 $evidence.Result='passed';$evidence.Limit='Cold actual Main HWND visible in the CI windowstation/normal close/reopen only; noninteractive sessions are recorded, not claimed as human desktop interaction. Known OpenSource version information is observed, never clicked. Mutex-before-import, the safe official-update refusal and raw authoritative LF account preservation are checked; GPU/memory utilities, authentication, navigation/font feel and update-bridge completion are not inferred.'
 if($ExerciseInteraction){$evidence.InteractionScope='Opt-in external probe: exact reviewed local information notice dismissal, safe Main settings/personalization/launch navigation, observable theme/INI restore, measured wheel offsets, focus and same-HWND minimize/restore; stable owned-window screenshots. Human feel/hover/drop/authentication/game/services/hardware remain pending.';$evidence.Limit='Original startup/mutex/import/account/close/reopen/file-lock contracts remain required. Only exact reviewed release/development information notices are dismissed in opt-in mode. Per-pass interaction evidence describes observed UIA/layout/own-config/screenshots in the CI windowstation; no human desktop or complete R003 pass is inferred.'}
}catch{if($evidence.Result -ne 'pending' -or $_.Exception.Message -notlike 'PENDING:*'){$evidence.Result='failed'};$evidence.Error=$_.Exception.Message;throw}
finally{
 try{
  $diagnosticExit=$null
  if($null -ne $nativeProcess -and $nativeProcess.HasExited){$diagnosticExit=$nativeProcess.ExitCode}elseif($null -ne $run -and $run.Contains('ExitCode')){$diagnosticExit=$run.ExitCode}
  $diagnosticLog=Read-OwnedLog
  if($diagnosticLog.Length -gt 0){Write-OwnedCloseDiagnostic $diagnosticExit $diagnosticLog 'before-cleanup'}
 }catch{$evidence.CleanupErrors+=('Owned bounded diagnostics: '+$_.Exception.Message);Write-Output ('Native diagnostic collection failed: '+$_.Exception.Message)}
 if($ExerciseInteraction -and -not $interactionObserverStopped){$ownProcessStopped=$false;$evidence.Result='failed';$evidence.CleanupErrors+='Main close/Dispose, mutex observer release and owned profile deletion skipped because interaction observer exit was not proved.'}
 if($null -ne $nativeProcess -and (-not $ExerciseInteraction -or $interactionObserverStopped)){try{if(-not $nativeProcess.HasExited){[void]$nativeProcess.CloseMainWindow();if(-not $nativeProcess.WaitForExit(3000)){$evidence.ForcedCleanup=$true;$nativeProcess.Kill();[void]$nativeProcess.WaitForExit(5000)}};$ownProcessStopped=$nativeProcess.HasExited;if(-not $ownProcessStopped){$evidence.CleanupErrors+='Own launcher is still alive; preserve fixture profile.'}}catch{$ownProcessStopped=$false;$evidence.CleanupErrors+=('Own process cleanup: '+$_.Exception.Message)}finally{$nativeProcess.Dispose()}}
 if($null -ne $startupMutexObserver){
  if($ownProcessStopped -and (-not $ExerciseInteraction -or $interactionObserverStopped)){try{$startupMutexObserver.Dispose();$startupMutexObserver=$null}catch{$evidence.CleanupErrors+=('Owned startup mutex/HWND cleanup: '+$_.Exception.Message)}}
  else{$evidence.CleanupErrors+='Startup mutex/HWND context retained because owned process stop was not proved.'}
 }
 # Delete only initially absent stores owned by this fixture marker. Never back
 # up/read/restore a real user's profile or delete an unmarked changed store.
 if($ownProcessStopped -and (-not $ExerciseInteraction -or $interactionObserverStopped)){foreach($keyName in $ownedRegistry){try{$node=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyName,$false);$matches=$false;try{$matches=$null -ne $node -and $node.GetValue($marker) -ceq $id}finally{if($null -ne $node){$node.Dispose()}};if($matches){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($keyName)}else{$evidence.CleanupErrors+=('Registry owner marker changed: '+$keyName)}}catch{$evidence.CleanupErrors+=('Registry cleanup: '+$keyName+' / '+$_.Exception.Message)}}
 if($null -ne $ownedAppData){try{if([IO.File]::ReadAllText((Join-Path $ownedAppData $marker)) -ceq $id){[IO.Directory]::Delete($ownedAppData,$true)}else{$evidence.CleanupErrors+='AppData owner marker changed.'}}catch{$evidence.CleanupErrors+=('Owned AppData cleanup: '+$_.Exception.Message)}}
 }else{$evidence.CleanupErrors+='Owned profile retained because process exit could not be confirmed.'}
 if($evidence.ForcedCleanup -or $evidence.CleanupErrors.Count -gt 0){if($evidence.Result -eq 'passed'){$evidence.Result='failed'}}
 if([IO.Directory]::Exists($fixture)){[IO.File]::WriteAllText((Join-Path $fixture 'evidence.json'),($evidence|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false));Write-Output ('Native Main smoke evidence: '+(Join-Path $fixture 'evidence.json'))}
}
if($evidence.Result -ne 'passed'){throw ('Native Main smoke '+$evidence.Result+'; see evidence.json')}
Write-Output 'PASS actual native PCL.Application Main: real startup/BAML/visible HWND in CI windowstation/log stages, WM_CLOSE normal controller exit, session/file locks, reopen/log rotation, safe --update exclusion, mutex-before-import and thirteen synthetic LF account raw values/kinds preserved. No update accepted.'
