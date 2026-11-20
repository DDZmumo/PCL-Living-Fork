# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public License,
# v. 2.0. If a copy of the MPL was not distributed with this file, you can
# obtain one at https://mozilla.org/MPL/2.0/.
param(
    [Parameter(Mandatory=$true)][string]$Asm,
    [Parameter(Mandatory=$true)][string]$AsmTree,
    [string]$JdkBin,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).ProviderPath
$asmCorePath = (Resolve-Path -LiteralPath $Asm).ProviderPath
$asmTreePath = (Resolve-Path -LiteralPath $AsmTree).ProviderPath
$javac = if ($JdkBin) { Join-Path $JdkBin 'javac.exe' } else { (Get-Command javac -ErrorAction Stop).Source }
$java = if ($JdkBin) { Join-Path $JdkBin 'java.exe' } else { (Get-Command java -ErrorAction Stop).Source }
$jar = if ($JdkBin) { Join-Path $JdkBin 'jar.exe' } else { (Get-Command jar -ErrorAction Stop).Source }
if (!$OutputPath) { $OutputPath = Join-Path $repo 'PCLCS/Launch/ModKeybindPatch.jar' }
$output = [IO.Path]::GetFullPath($OutputPath)
$stage = Join-Path $repo ('artifacts/mod-keybind-agent-build-' + [Guid]::NewGuid().ToString('N'))
$classes = Join-Path $stage 'classes'
$manifestDir = Join-Path $classes 'META-INF'
[IO.Directory]::CreateDirectory($manifestDir) | Out-Null
$sources = @('Agent.java', 'SaveTransformer.java', 'KeybindWriter.java') | ForEach-Object { Join-Path $PSScriptRoot ('src/' + $_) }
$compileClasspath = $asmCorePath + [IO.Path]::PathSeparator + $asmTreePath
& $javac --release 21 -encoding UTF-8 -proc:none -g:none -Xlint:all -Werror -classpath $compileClasspath -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw 'Mod keybind patch compilation failed.' }
$tests = Join-Path $stage 'tests'
$testSource = Join-Path $PSScriptRoot 'tests/KeybindWriterTest.java'
& $javac --release 21 -encoding UTF-8 -proc:none -Xlint:all -Werror -classpath $classes -d $tests $testSource
if ($LASTEXITCODE -ne 0) { throw 'Mod keybind patch test compilation failed.' }
& $java -classpath ($classes + [IO.Path]::PathSeparator + $tests) org.pcllf.keybinds.io.KeybindWriterTest $stage
if ($LASTEXITCODE -ne 0) { throw 'Mod keybind patch writer checks failed.' }
$manifest = "Manifest-Version: 1.0`r`nPremain-Class: org.pcllf.keybinds.Agent`r`n`r`n"
[IO.File]::WriteAllText((Join-Path $manifestDir 'MANIFEST.MF'), $manifest, [Text.UTF8Encoding]::new($false))
$jarPath = Join-Path $stage 'ModKeybindPatch.jar'
& $jar --create --no-manifest --file $jarPath --date '1980-01-01T00:00:02Z' -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'Mod keybind patch packaging failed.' }
[IO.File]::Copy($jarPath, $output, $true)
Get-FileHash -LiteralPath $output -Algorithm SHA256
