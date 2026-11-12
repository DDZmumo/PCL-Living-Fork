# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public License,
# v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
# one at https://mozilla.org/MPL/2.0/.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Resolve-VbCompiler.ps1')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $repo ('artifacts/compiler-discovery-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$explicit = Join-Path $fixture 'explicit/vbc.exe'
$pathCompiler = Join-Path $fixture 'path/vbc.exe'
$customCompiler = Join-Path $fixture 'Custom VS Enterprise/MSBuild/Current/Bin/Roslyn/vbc.exe'
foreach ($file in @($explicit,$pathCompiler,$customCompiler)) {
    New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
    [IO.File]::WriteAllBytes($file,[byte[]]@())
}
$argumentFile = Join-Path $fixture 'vswhere-args.json'
$locator = Join-Path $fixture 'vswhere-fixture.ps1'
$content = '$args | ConvertTo-Json -Compress | Set-Content -LiteralPath ''' + $argumentFile.Replace("'","''") + '''' + "`n" +
    'Write-Output ''' + $customCompiler.Replace("'","''") + '''' + "`nexit 0`n"
[IO.File]::WriteAllText($locator,$content,[Text.UTF8Encoding]::new($true))
$script:mode = 'explicit'
function Get-Command {
    param([string]$Name,$CommandType,$ErrorAction)
    if ($script:mode -eq 'explicit') { throw 'Explicit override unexpectedly queried machine installations.' }
    if ($Name -eq 'vbc.exe' -and $script:mode -eq 'path') { return [pscustomobject]@{Source=$pathCompiler} }
    if ($Name -eq 'vswhere.exe') { return [pscustomobject]@{Source=if($script:mode -eq 'vswhere'){$locator}else{Join-Path $fixture 'missing-vswhere.exe'}} }
    return $null
}
function Check([bool]$condition,[string]$message) { if (-not $condition) { throw $message } }
Check ((Resolve-VbCompiler -VbcPath $explicit) -ceq $explicit) 'Explicit compiler override was not preserved.'
try { Resolve-VbCompiler -VbcPath (Join-Path $fixture 'missing.exe'); throw 'Invalid explicit compiler accepted.' }
catch { Check ($_.Exception.Message -like '*explicit -VbcPath*') 'Invalid override silently fell back to machine state.' }
$script:mode='path'
Check ((Resolve-VbCompiler) -ceq $pathCompiler) 'PATH application lookup was not preferred.'
$script:mode='vswhere'
Check ((Resolve-VbCompiler) -ceq $customCompiler) 'Custom/Enterprise/Build Tools discovery failed.'
$forwarded=Get-Content -LiteralPath $argumentFile -Raw | ConvertFrom-Json
foreach ($argument in @('-latest','-products','*','-requires','Microsoft.Component.MSBuild','-find','MSBuild\**\Bin\Roslyn\vbc.exe')) {
    Check ($forwarded -ccontains $argument) ('Missing vswhere constraint: '+$argument)
}
$script:mode='missing'
try { Resolve-VbCompiler; throw 'Missing compiler accepted.' }
catch { Check ($_.Exception.Message -like '*VB compiler not found*') 'Missing tools did not give an actionable error.' }
Write-Output 'PASS VB compiler discovery: explicit override/missing override, PATH priority, edition-independent custom vswhere and actionable missing-tools failure; no compiler or launcher executed.'
