param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputPath,
    [string]$MSBuildPath = 'msbuild'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$generatedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Plain Craft Launcher 2\obj\CredentialBuild'))
$generatedFolder = Join-Path $generatedRoot ([Guid]::NewGuid().ToString('N'))
$generatedSource = Join-Path $generatedFolder 'BuildCredentials.g.vb'
$buildLease = $null
try {
    [IO.Directory]::CreateDirectory($generatedRoot) | Out-Null
    try {
        $buildLease = [IO.File]::Open((Join-Path $generatedRoot 'build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw 'Cannot acquire the workspace build lock; another release build may be running.'
    }
    [IO.Directory]::CreateDirectory($generatedFolder) | Out-Null
    & (Join-Path $PSScriptRoot 'Write-BuildCredentials.ps1') -OutputPath $generatedSource
    $intermediatePath = Join-Path $generatedFolder 'Intermediate\'
    $buildArgs = @((Join-Path $projectRoot 'Plain Craft Launcher 2\PCL-Living-Fork.vbproj'), '-t:Rebuild', "-p:Configuration=$Configuration", "-p:GeneratedCredentialSource=$generatedSource", "-p:CredentialIntermediateOutputPath=$intermediatePath", '-verbosity:minimal', '-nologo')
    if ($OutputPath) { $buildArgs += "-p:OutputPath=$OutputPath" }
    & $MSBuildPath @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
    # Keep the legacy assembly identity, but use the public product name on disk.
    $projectFolder=Join-Path $projectRoot 'Plain Craft Launcher 2'
    $outputFolder=if($OutputPath){if([IO.Path]::IsPathRooted($OutputPath)){[IO.Path]::GetFullPath($OutputPath)}else{[IO.Path]::GetFullPath((Join-Path $projectFolder $OutputPath))}}else{Join-Path $projectFolder 'bin'}
    foreach($suffix in @('', '.config')){
        $compiled=Join-Path $outputFolder ('LivingPCL.exe'+$suffix)
        if([IO.File]::Exists($compiled)){Move-Item -LiteralPath $compiled -Destination (Join-Path $outputFolder ('PCL Living Fork.exe'+$suffix)) -Force}
    }
}
finally {
    try {
        if ([IO.Directory]::Exists($generatedFolder)) {
            $resolvedFolder = [IO.Path]::GetFullPath($generatedFolder)
            $rootPrefix = $generatedRoot.TrimEnd('\') + '\'
            if (-not $resolvedFolder.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolvedFolder) -notmatch '\A[0-9a-f]{32}\z' -or
                (([IO.File]::GetAttributes($resolvedFolder) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw 'Unsafe credential-build cleanup path'
            }
            [IO.Directory]::Delete($resolvedFolder, $true)
        }
    } finally {
        if ($null -ne $buildLease) { $buildLease.Dispose() }
    }
}
