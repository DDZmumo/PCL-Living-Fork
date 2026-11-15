param(
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('Debug','Release','Snapshot')][string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [switch]$OfficialRelease,
    [switch]$EnableLegacyLink
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Select a new output directory for the slim package.' }
$stage = Join-Path $output 'publish'
& (Join-Path $PSScriptRoot 'Build-ModernRelease.ps1') -Configuration $Configuration -ProjectPath $ProjectPath -OutputPath $stage -DotNetPath $DotNetPath -Slim -OfficialRelease:$OfficialRelease -EnableLegacyLink:$EnableLegacyLink
$payload = Join-Path $stage 'LivingPCL.exe'
# Incremental referenced-project output may include PDBs even with DebugType=none.
# They stay in the build staging directory; never omit a runtime DLL/other file
# to make the single-file package appear complete.
$unexpected = @(Get-ChildItem -LiteralPath $stage -Force | Where-Object {
    $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    ($_.Name -cne 'LivingPCL.exe' -and $_.Extension -ine '.pdb')
})
if (-not [IO.File]::Exists($payload) -or $unexpected.Count -ne 0) { throw 'Slim single-file publish produced an unexpected layout.' }
$inspectorOutput = Join-Path $output 'verification-tools'
& $DotNetPath build (Join-Path $PSScriptRoot 'native-update-inspector/NativeUpdateInspector.csproj') -c Release --output $inspectorOutput --disable-build-servers -nodeReuse:false -m:1 -warnaserror -v:q
if ($LASTEXITCODE -ne 0) { throw 'The slim product identity inspector failed to build.' }
$identityText = & $DotNetPath (Join-Path $inspectorOutput 'NativeUpdateInspector.dll') $payload
if ($LASTEXITCODE -ne 0) { throw 'The genuine slim bundle failed product identity checks.' }
$identity = ($identityText -join "`n") | ConvertFrom-Json
$version = [string]$identity.ProductVersion
if ($version -ne 'dev_edition' -and $version -notmatch '\A\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') { throw 'Invalid slim product version.' }
$fileName = 'PCL-Living-Fork-' + $version + '.exe'
$executable = Join-Path $output $fileName
# The canonical EXE is also selected by already-published Framework launchers.
# A native apphost cannot pass their managed assembly check, even without a runtime.
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
& (Join-Path $PSScriptRoot 'Build-UpdateBridge.ps1') -ExecutablePath $payload -OutputPath $executable -DotNetPath $DotNetPath
if ([Reflection.AssemblyName]::GetAssemblyName($executable).Name -cne 'LivingPCL' -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductName -cne 'LivingPCL') {
    throw 'The public EXE must retain the old-client managed update identity.'
}
$archive = Join-Path $output ('PCL-Living-Fork-' + $version + '-win-x64.zip')
# First-install ZIP still contains the same slim native payload, named canonically.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $payload, $fileName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
} finally { $zip.Dispose() }
$records = @()
foreach ($item in @(@{role='slim-exe';path=$executable}, @{role='slim-zip';path=$archive})) {
    $records += [ordered]@{role=$item.role;fileName=[IO.Path]::GetFileName($item.path);sha256=(Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $item.path).Length}
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), (($records | ForEach-Object { $_.sha256 + '  ' + $_.fileName }) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
[ordered]@{kind='PCLLF.SlimRelease';productVersion=$version;runtime='Microsoft.WindowsDesktop.App 10.0 x64';selfContained=$false;legacyBridgeIncluded=$true;nativePayloadSha256=$payloadHash;artifacts=$records} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SlimRelease.Artifacts.json') -Encoding utf8
Write-Output ('Slim product requires .NET 10 Windows Desktop Runtime: ' + $executable)
