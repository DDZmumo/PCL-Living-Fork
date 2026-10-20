param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputPath,
    [string]$MSBuildPath = 'msbuild'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$secretPath = Join-Path $projectRoot 'Plain Craft Launcher 2\Modules\ModSecret.vb'
$originalBytes = [IO.File]::ReadAllBytes($secretPath)
try {
    $source = [IO.File]::ReadAllText($secretPath)
    $settings = @{
        OAuthClientId = 'PCL_MS_CLIENT_ID'
        CurseForgeAPIKey = 'PCL_CURSEFORGE_API_KEY'
    }
    foreach ($field in $settings.Keys) {
        $variable = $settings[$field]
        $value = [Environment]::GetEnvironmentVariable($variable)
        if ([string]::IsNullOrEmpty($value)) { continue }
        if ($value.Contains("`r") -or $value.Contains("`n")) { throw "Invalid multiline build setting: $variable" }
        $literal = $value.Replace('"', '""')
        $replacement = 'Public ' + $field + ' As String = If(Environment.GetEnvironmentVariable("' + $variable + '"), "' + $literal + '")'
        $pattern = 'Public ' + $field + ' As String = [^\r\n]*'
        $source = [regex]::Replace($source, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement })
    }
    [IO.File]::WriteAllText($secretPath, $source, [Text.UTF8Encoding]::new($true))
    $buildArgs = @((Join-Path $projectRoot 'Plain Craft Launcher 2\LivingPCL2-Re.vbproj'), '-t:Rebuild', "-p:Configuration=$Configuration", '-verbosity:minimal', '-nologo')
    if ($OutputPath) { $buildArgs += "-p:OutputPath=$OutputPath" }
    & $MSBuildPath @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
}
finally {
    [IO.File]::WriteAllBytes($secretPath, $originalBytes)
}
