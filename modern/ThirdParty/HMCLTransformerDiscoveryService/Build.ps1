# HMCL Transformer Discovery Service source packaging helper.
# Copyright (C) 2026 PCL Living Fork contributors
# SPDX-License-Identifier: GPL-3.0-or-later
# Added 2026-11-08. Upstream Java source remains unchanged.
[CmdletBinding()]
param(
    [string]$JdkBin = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)
$ErrorActionPreference = 'Stop'
$javac = if ($JdkBin) { Join-Path $JdkBin 'javac.exe' } else { (Get-Command javac -ErrorAction Stop).Source }
$jar = if ($JdkBin) { Join-Path $JdkBin 'jar.exe' } else { (Get-Command jar -ErrorAction Stop).Source }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$apiClasses = Join-Path $outputPath 'compile-api'
$classes = Join-Path $outputPath 'classes'
New-Item -ItemType Directory -Path $apiClasses, $classes -Force | Out-Null

& $javac --release 8 -encoding UTF-8 -g -d $apiClasses (Join-Path $PSScriptRoot 'compile-api/ITransformerDiscoveryService.java')
if ($LASTEXITCODE -ne 0) { throw 'ModLauncher API compilation failed.' }
& $javac --release 8 -encoding UTF-8 -g -classpath $apiClasses -d $classes (Join-Path $PSScriptRoot 'src/main/java/org/jackhuang/hmcl/HMCLTransformerDiscoveryService.java')
if ($LASTEXITCODE -ne 0) { throw 'HMCL Transformer Discovery Service compilation failed.' }
$serviceDirectory = Join-Path $classes 'META-INF/services'
New-Item -ItemType Directory -Path $serviceDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/main/resources/META-INF/services/cpw.mods.modlauncher.serviceapi.ITransformerDiscoveryService') -Destination $serviceDirectory
$jarPath = Join-Path $outputPath 'HMCLTransformerDiscoveryService-1.0.jar'
& $jar --create --file $jarPath -C $classes 'org/jackhuang/hmcl/HMCLTransformerDiscoveryService.class' -C $classes 'META-INF/services/cpw.mods.modlauncher.serviceapi.ITransformerDiscoveryService'
if ($LASTEXITCODE -ne 0) { throw 'HMCL Transformer Discovery Service archive creation failed.' }
Write-Output $jarPath
