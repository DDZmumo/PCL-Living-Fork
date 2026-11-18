# Original LF host tooling; repository LICENCE applies.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Manifest,
    [ValidateSet('java8','java21')][string[]]$Cases = @('java8','java21'),
    [ValidateRange(3,10)][int]$Rounds = 3,
    [string]$Executable,
    [switch]$DiagnosticSplit
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $repository 'tests\PCLLF.Mmc.Performance.Probe\bin\Release\net10.0-windows\PCLLF.Mmc.Performance.Probe.exe' }
$executablePath = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'Build the real host probe explicitly before measuring; this script never builds during a baseline.' }
$manifestPath = [IO.Path]::GetFullPath($Manifest)
$reportRoot = Join-Path $repository ('artifacts\mmc-performance\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$start = [DateTime]::UtcNow
# One executable at a time. Each fresh process runs the cold iteration and then one hot iteration.
foreach ($case in $Cases) {
    for ($round=1; $round -le $Rounds; $round++) {
        $report = Join-Path $reportRoot ($case + '-round-' + $round + '.json')
        $arguments = @('--authorize-owned-fixture','--manifest',$manifestPath,'--case',$case,'--output',$report,'--repetitions','2')
        if ($DiagnosticSplit) { $arguments += '--diagnostic-split' }
        & $executablePath @arguments
        if ($LASTEXITCODE -ne 0) { throw ('Probe failed; preserve evidence at ' + $reportRoot) }
    }
}
function Get-Median([double[]]$Values) { $sorted=@($Values | Sort-Object); if ($sorted.Count % 2) { $sorted[[int][Math]::Floor($sorted.Count/2)] } else { ($sorted[$sorted.Count/2-1]+$sorted[$sorted.Count/2])/2 } }
$all = @(Get-ChildItem -LiteralPath $reportRoot -Filter '*-round-*.json' | ForEach-Object { $document=Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json; foreach ($run in $document.Runs) { [pscustomobject]@{Case=$document.Host.Name;Temperature=$run.Temperature;Run=$run} } })
if ($all.Count -ne $Cases.Count*$Rounds*2 -or @($all | Where-Object { -not $_.Run.Success }).Count) { throw 'Incomplete or failed runs cannot produce a successful median baseline.' }
$summary = foreach ($group in ($all | Group-Object Case,Temperature)) {
    $runs=@($group.Group.Run)
    $stages = foreach ($stageGroup in ($runs.Stages | Group-Object Name)) { [ordered]@{ Name=$stageGroup.Name; Samples=$stageGroup.Count; MedianElapsedMs=(Get-Median @($stageGroup.Group.ElapsedMs)); MedianCpuMs=(Get-Median @($stageGroup.Group.CpuMs)); MedianLogicalReadBytes=(Get-Median @($stageGroup.Group.ReadTransferBytes)); MedianLogicalWriteBytes=(Get-Median @($stageGroup.Group.WriteTransferBytes)); MaximumProcessPeakWorkingSet=($stageGroup.Group.ProcessPeakWorkingSet | Measure-Object -Maximum).Maximum } }
    [ordered]@{ Case=$group.Group[0].Case; Temperature=$group.Group[0].Temperature; Samples=$runs.Count; Stages=@($stages); DownloadRequests=($runs.DownloadRequests | Measure-Object -Sum).Sum; MetadataNetworkAttempts=($runs.MetadataNetworkAttempts | Measure-Object -Sum).Sum; ProviderPreferenceChanges=@($runs | Where-Object ProviderPreferencesChanged).Count; SampledOwnedPeakLogicalBytes=($runs.SampledOwnedPeakLogicalBytes | Measure-Object -Maximum).Maximum }
}
$result=[ordered]@{SchemaVersion=1;Kind='PCLLF.MmcPerformanceMedian';StartedUtc=$start;CompletedUtc=[DateTime]::UtcNow;ManifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();Rounds=$Rounds;Definitions='Process-cold means fresh executable process with ordinary uncontrolled OS cache. Hot means the second iteration in that same process. Diagnostic split stages are separate warm observations, not additive parts of prepare.';Groups=@($summary)}
$summaryPath=Join-Path $reportRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath,($result | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output $summaryPath
