param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$LegacyExecutablePath)
# Hidden fixture processes only. Never runs the real launcher's entry point.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclBridgeTest-'+[Guid]::NewGuid().ToString('N'))
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
$oldNoUi=[Environment]::GetEnvironmentVariable('PCL_UPDATE_BRIDGE_NO_UI','Process')
function Start-Fixture([string]$path,[string]$arguments){
 $info=[Diagnostics.ProcessStartInfo]::new($path,$arguments)
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.WorkingDirectory=[IO.Path]::GetDirectoryName($path)
 $process=[Diagnostics.Process]::Start($info);$processes.Add($process)
 return $process
}
function Wait-File([string]$path){
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while(-not [IO.File]::Exists($path)) {if($timer.ElapsedMilliseconds -gt 15000){throw 'Fixture process did not become ready'};Start-Sleep -Milliseconds 30}
}
function Hash([string]$path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
 $compiler=(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\csc.exe'|Select-Object -First 1)
 if(-not $compiler){throw 'Visual Studio C# compiler unavailable'}
 $parentSource=Join-Path $fixture 'Parent.cs'
 [IO.File]::WriteAllText($parentSource,@'
using System;
using System.IO;
using System.Threading;
using System.Reflection;
[assembly:AssemblyProduct("LivingPCL")]
[assembly:AssemblyVersion("1.0.0.0")]
internal static class Parent {
 static void Main() {
  string root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
  File.WriteAllText(Path.Combine(root,"ready"),"ready");
  for(int i=0;i<1000&&!File.Exists(Path.Combine(root,"exit"));i++)Thread.Sleep(30);
 }
}
'@)
 $payloadSource=Join-Path $fixture 'Payload.cs'
 [IO.File]::WriteAllText($payloadSource,@'
using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
[assembly:AssemblyProduct("PCL Living Fork")]
[assembly:AssemblyVersion("99.0.0.0")]
[assembly:AssemblyFileVersion("99.0.0.0")]
internal static class Payload {
 static int Main(string[] args) {
  if(args.Length!=4||args[0]!="--livingpcl-update")return 20;
  string self=Assembly.GetExecutingAssembly().Location;
  string root=Directory.GetParent(Path.GetDirectoryName(self)).Parent.FullName;
  string target=Path.Combine(root,args[2]);
  using(var sha=SHA256.Create())using(var file=File.OpenRead(self))
   if(BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant()!=args[3])return 21;
  try {using(var parent=Process.GetProcessById(int.Parse(args[1])))if(!parent.WaitForExit(15000))return 22;}catch(ArgumentException){}
  File.Copy(self,target,true);
  File.WriteAllLines(Path.Combine(root,"received-args"),args);
  return 0;
 }
}
'@)
 $parentBinary=Join-Path $fixture 'Parent/LivingPCL.exe'
 $payload=Join-Path $fixture 'Payload/LivingPCL.exe'
 foreach($path in @($parentBinary,$payload)){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null}
 & $compiler /nologo /target:winexe /optimize+ ('/out:'+$parentBinary) $parentSource
 if($LASTEXITCODE -ne 0){throw 'Parent fixture compile failed'}
 & $compiler /nologo /target:winexe /optimize+ ('/out:'+$payload) $payloadSource
 if($LASTEXITCODE -ne 0){throw 'Payload fixture compile failed'}
 $bridge=Join-Path $fixture 'Bridge/LivingPCL.exe'
 & (Join-Path $PSScriptRoot 'Build-UpdateBridge.ps1') -ExecutablePath $payload -OutputPath $bridge
 if([Reflection.AssemblyName]::GetAssemblyName($bridge).Name -ne 'LivingPCL' -or [Diagnostics.FileVersionInfo]::GetVersionInfo($bridge).ProductName -ne 'LivingPCL'){throw 'Legacy identity was lost'}
 if($LegacyExecutablePath){
  $root=Split-Path $PSScriptRoot -Parent
  foreach($path in @('MeloongCore/Shared/bin/MeloongCore.dll','MeloongCore/Wpf/bin/MeloongCore.Wpf.dll','PCLCS/bin/PCLCS.dll','Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $root $path))|Out-Null}
  $legacy=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($LegacyExecutablePath))
  $validate=$legacy.GetType('PCL.ModLauncherUpdate',$true).GetMethod('ValidateExecutable',[Reflection.BindingFlags]'Public,NonPublic,Static')
  $validate.Invoke($null,@([string]$bridge,[string](Hash $bridge)))|Out-Null
  $rejected=$false;try{$validate.Invoke($null,@([string]$ExecutablePath,[string](Hash $ExecutablePath)))|Out-Null}catch{$rejected=$true}
  if($rejected){Write-Output 'Published legacy client requires the compatibility product identity.'}
  else{Write-Output 'Published legacy client accepts both direct and compatibility payload identities.'}
 }
 $env:PCL_UPDATE_BRIDGE_NO_UI='1'
 $install=Join-Path $fixture 'Legacy Install'
 $updates=Join-Path $install 'PCL/Updates';[IO.Directory]::CreateDirectory($updates)|Out-Null
 $target=Join-Path $install 'Old Launcher Name.exe'
 [IO.File]::Copy($parentBinary,$target)
 $settings=Join-Path $install 'PCL/Setup.ini';[IO.File]::WriteAllText($settings,'synthetic-settings')
 $game=Join-Path $install '.minecraft/sentinel.txt';[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($game))|Out-Null;[IO.File]::WriteAllText($game,'synthetic-game')
 $staged=Join-Path $updates 'LivingPCL2-Re-99.0.0.exe';[IO.File]::Copy($bridge,$staged)
 $parent=Start-Fixture $target '';Wait-File (Join-Path $install 'ready')
 $helper=Start-Fixture $staged ('--livingpcl-update '+$parent.Id+' "Old Launcher Name.exe" '+(Hash $staged))
 if($helper.WaitForExit(500)){throw 'Bridge exited before the old updater startup check'}
 [IO.File]::WriteAllText((Join-Path $install 'exit'),'exit')
 if(-not $helper.WaitForExit(20000) -or $helper.ExitCode -ne 0){throw 'Legacy bridge handoff failed'}
 if((Hash $target) -cne (Hash $payload)){throw 'Bridge did not install the embedded payload'}
 $received=[IO.File]::ReadAllLines((Join-Path $install 'received-args'))
 if($received[2] -cne 'Old Launcher Name.exe' -or $received[3] -cne (Hash $payload)){throw 'Handoff changed target name or used the wrapper checksum'}
 if([IO.File]::ReadAllText($settings) -cne 'synthetic-settings' -or [IO.File]::ReadAllText($game) -cne 'synthetic-game'){throw 'Bridge changed user data'}
 $manual=Join-Path $fixture 'Manual';[IO.Directory]::CreateDirectory($manual)|Out-Null
 $manualExe=Join-Path $manual 'Downloaded Bridge.exe';[IO.File]::Copy($bridge,$manualExe)
 $manualProcess=Start-Fixture $manualExe ''
 if(-not $manualProcess.WaitForExit(10000) -or $manualProcess.ExitCode -ne 0){throw 'Manual bridge launch failed'}
 Wait-File (Join-Path $manual 'received-args')
 if((Hash $manualExe) -cne (Hash $payload)){throw 'Manual download was not replaced with the branded payload'}
 $before=Hash $target
 foreach($case in @(@('Old Launcher Name.exe',('0'*64)),@('../foreign.exe',(Hash $staged)))){
  $invalid=Start-Fixture $staged ('--livingpcl-update '+$PID+' "'+$case[0]+'" '+$case[1])
  if(-not $invalid.WaitForExit(10000) -or $invalid.ExitCode -eq 0 -or (Hash $target) -cne $before){throw 'Invalid bridge parameters changed the installed launcher'}
 }
 $validation=if($LegacyExecutablePath){'published legacy validation, '}else{''}
 Write-Output ('Update bridge passed: '+$validation+'500-ms startup, checksum handoff, target names with spaces, automatic/manual replacement and invalid-input preservation. Only hidden fixtures were launched; game/settings sentinels unchanged.')
}finally{
 [Environment]::SetEnvironmentVariable('PCL_UPDATE_BRIDGE_NO_UI',$oldNoUi,'Process')
 foreach($process in $processes){try{if(-not $process.HasExited){$process.Kill();$process.WaitForExit(5000)|Out-Null};$process.Dispose()}catch{}}
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclBridgeTest-[a-f0-9]{32}\z'){throw 'Unsafe bridge fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
