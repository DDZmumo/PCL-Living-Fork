param(
 [Parameter(Mandatory=$true)][string]$ExecutablePath,
 [Parameter(Mandatory=$true)][string]$OutputPath,
 [string]$DotNetPath='dotnet',
 [string]$NativeInspectorProject,
 [string]$NativeUpdaterSource
)
# The canonical old-client asset remains a managed LivingPCL bridge. Both normal
# managed payloads and our genuine NET10 single-file payload use the same legacy
# four-argument handoff, embedded bytes, checksum and replacement transaction.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('PclUpdateBridge-'+[Guid]::NewGuid().ToString('N'))
try{
 [IO.Directory]::CreateDirectory($temporary)|Out-Null
 $identity=$null
 try{$identity=[Reflection.AssemblyName]::GetAssemblyName($ExecutablePath)}catch{
  # Preserve the API exception: its BadImageFormatException can contain a
  # PEReader InvalidOperationException. GetBaseException would discard it.
  $metadataError=$_.Exception
  if($metadataError -is [Management.Automation.MethodInvocationException]){$metadataError=$metadataError.InnerException}
  if($metadataError -isnot [BadImageFormatException]){throw}
 }
 if($null -ne $identity){
  # Preserve the original managed candidate's exact identity/product checks.
  $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
  if($identity.Name -ne 'LivingPCL' -or $version.ProductName -ne 'PCL Living Fork'){throw 'Bridge payload must be the normal PCL Living Fork build'}
  $parsed=[version]$version.FileVersion
 }else{
  if(-not $NativeInspectorProject){$NativeInspectorProject=Join-Path $PSScriptRoot 'native-update-inspector/NativeUpdateInspector.csproj'}
  $inspectorFolder=Join-Path $temporary 'Inspector'
  $inspectArgs=@('build',[IO.Path]::GetFullPath($NativeInspectorProject),'-c','Release','--output',$inspectorFolder,'-warnaserror','-v:q')
  if($NativeUpdaterSource){$inspectArgs+=('-p:LFNativeUpdaterSource='+[IO.Path]::GetFullPath($NativeUpdaterSource))}
  & $DotNetPath @inspectArgs
  if($LASTEXITCODE -ne 0){throw 'Native payload inspector compilation failed'}
  $identityJson=& $DotNetPath (Join-Path $inspectorFolder 'NativeUpdateInspector.dll') $ExecutablePath
  if($LASTEXITCODE -ne 0){throw 'Native bridge payload is not a supported genuine LF bundle'}
  $nativeIdentity=($identityJson -join "`n")|ConvertFrom-Json
  if($nativeIdentity.AssemblyName -ne 'LivingPCL' -or $nativeIdentity.Product -ne 'PCL Living Fork'){throw 'Bridge payload must be the normal PCL Living Fork build'}
  # Use the actual embedded entry attribute, not the native shell's caption.
  $parsed=[version]$nativeIdentity.FileVersion
 }
 $hash=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
 $payloadLength=(Get-Item -LiteralPath $ExecutablePath).Length
 $packed=Join-Path $temporary 'Payload.gz'
 $output=[IO.File]::Create($packed)
 try {
  $gzip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionLevel]::Optimal)
  try {
   $inputFile=[IO.File]::OpenRead($ExecutablePath)
   try { $inputFile.CopyTo($gzip) } finally { $inputFile.Dispose() }
  } finally { $gzip.Dispose() }
 } finally { $output.Dispose() }
 $info=Join-Path $temporary 'PayloadInfo.cs'
 $text=@"
using System.Reflection;
[assembly:AssemblyTitle("PCL Living Fork 更新兼容程序")]
[assembly:AssemblyProduct("LivingPCL")]
[assembly:AssemblyVersion("$parsed")]
[assembly:AssemblyFileVersion("$parsed")]
internal static class PayloadInfo { internal const string Sha256 = "$hash"; internal const long Length = ${payloadLength}L; }
"@
 [IO.File]::WriteAllText($info,$text,[Text.UTF8Encoding]::new($false))
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
 $compiler=(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\csc.exe'|Select-Object -First 1)
 if(-not $compiler){throw 'Visual Studio C# compiler unavailable'}
 $icon=Join-Path (Split-Path $PSScriptRoot -Parent) 'modern/Main/Images/Branding/LivingPCL.ico'
 $compiled=Join-Path $temporary 'LivingPCL.exe'
 & $compiler /nologo /target:winexe /optimize+ ('/out:'+$compiled) ('/win32icon:'+$icon) ('/resource:'+$packed+',PCLLivingFork.Payload.gz') /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'update-bridge/Program.cs') $info
 if($LASTEXITCODE -ne 0){throw 'Update bridge compilation failed'}
 if([Reflection.AssemblyName]::GetAssemblyName($compiled).Name -ne 'LivingPCL' -or [Diagnostics.FileVersionInfo]::GetVersionInfo($compiled).ProductName -ne 'LivingPCL'){throw 'Legacy bridge identity mismatch'}
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))|Out-Null
 Copy-Item -LiteralPath $compiled -Destination $OutputPath -Force
 Write-Output 'Legacy update bridge built: old client identity retained, normal branded payload embedded with SHA256 verification.'
}finally{
 $full=[IO.Path]::GetFullPath($temporary);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\APclUpdateBridge-[a-f0-9]{32}\z'){throw 'Unsafe bridge build cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
