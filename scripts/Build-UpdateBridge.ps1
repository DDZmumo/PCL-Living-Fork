param([Parameter(Mandatory=$true)][string]$ExecutablePath,[Parameter(Mandatory=$true)][string]$OutputPath)
# Produces the legacy-repository EXE. No credentials are read: the already built
# application is embedded byte-for-byte with an authenticated download checksum.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$identity=[Reflection.AssemblyName]::GetAssemblyName($ExecutablePath)
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
if($identity.Name -ne 'LivingPCL' -or $version.ProductName -ne 'PCL Living Fork'){throw 'Bridge payload must be the normal PCL Living Fork build'}
$parsed=[version]$version.FileVersion
$hash=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('PclUpdateBridge-'+[Guid]::NewGuid().ToString('N'))
try{
 [IO.Directory]::CreateDirectory($temporary)|Out-Null
 $info=Join-Path $temporary 'PayloadInfo.cs'
 $text=@"
using System.Reflection;
[assembly:AssemblyTitle("PCL Living Fork 更新兼容程序")]
[assembly:AssemblyProduct("LivingPCL")]
[assembly:AssemblyVersion("$parsed")]
[assembly:AssemblyFileVersion("$parsed")]
internal static class PayloadInfo { internal const string Sha256 = "$hash"; }
"@
 [IO.File]::WriteAllText($info,$text,[Text.UTF8Encoding]::new($false))
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
 $compiler=(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\csc.exe'|Select-Object -First 1)
 if(-not $compiler){throw 'Visual Studio C# compiler unavailable'}
 $compiled=Join-Path $temporary 'LivingPCL.exe'
 & $compiler /nologo /target:winexe /optimize+ ('/out:'+$compiled) ('/resource:'+$ExecutablePath+',PCLLivingFork.Payload') /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'update-bridge/Program.cs') $info
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
