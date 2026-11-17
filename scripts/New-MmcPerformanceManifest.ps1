# Original LF host tooling; repository LICENCE applies.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AcceptanceRoot,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$accepted = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($AcceptanceRoot))
$output = [IO.Path]::GetFullPath($OutputPath)
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) { throw 'Use a new output below this checkout artifacts directory.' }
function Assert-NoLinks([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Performance evidence path contains a link.' }
    }
}
function Get-Digest([string]$Path) { Assert-NoLinks $Path; (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
Assert-NoLinks $accepted
$cases = foreach ($name in @('java8', 'java21')) {
    $caseRoot = Join-Path $accepted $name
    $ownerPath = Join-Path $caseRoot 'acceptance-owner.json'
    $sourcePath = Join-Path $caseRoot 'source-and-java.json'
    $importPath = Join-Path $caseRoot 'import-result.json'
    Assert-NoLinks $caseRoot
    foreach ($path in @($ownerPath,$sourcePath,$importPath)) { Assert-NoLinks $path }
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    $source = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
    $imported = Get-Content -LiteralPath $importPath -Raw | ConvertFrom-Json
    if ($owner.Kind -ne 'PCLLF.GtnhImportCase' -or -not $owner.Keep -or $owner.Sample -ne $name -or $owner.Root -ne $caseRoot -or $owner.MinecraftRoot -ne (Join-Path $caseRoot 'game') -or $imported.Status -ne 'Succeeded') { throw 'Fixture owner/import evidence differs.' }
    $instance = [IO.Path]::GetFullPath($imported.InstanceRoot)
    if ([IO.Path]::GetDirectoryName($instance) -ne (Join-Path $caseRoot 'game\versions')) { throw 'Instance is not a direct child of this owned fixture versions directory.' }
    $primaryPath = Join-Path $instance 'PCL\InstanceDefinition.json'
    Assert-NoLinks $primaryPath
    $primary = Get-Content -LiteralPath $primaryPath -Raw | ConvertFrom-Json
    if ($primary.instanceId -ne $imported.InstanceId -or $primary.origin.packageSha256 -ne $source.Sha256) { throw 'Current definition does not belong to its fixed import/source.' }
    [ordered]@{ Name=$name; CaseRoot=$caseRoot; OwnerSha256=(Get-Digest $ownerPath); SourceRecordSha256=(Get-Digest $sourcePath); ImportRecordSha256=(Get-Digest $importPath); PackageSha256=$source.Sha256; InstanceRoot=$instance; InstanceId=$primary.instanceId; Generation=$primary.generation; PrimarySha256=(Get-Digest $primaryPath) }
}
$manifest = [ordered]@{ SchemaVersion=1; Kind='PCLLF.MmcOwnedPerformanceManifest'; AuthorizedAcceptanceRoot=$accepted; Cases=@($cases) }
Assert-NoLinks $output
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllText($output, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output $output
