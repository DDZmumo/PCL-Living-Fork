# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public License,
# v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
# one at https://mozilla.org/MPL/2.0/.

function Resolve-VbCompiler {
    [CmdletBinding()]
    param([string]$VbcPath)
    if ($VbcPath) {
        if (-not [IO.File]::Exists($VbcPath)) { throw 'The explicit -VbcPath does not identify an existing compiler.' }
        return [IO.Path]::GetFullPath($VbcPath)
    }
    $command = Get-Command vbc.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -and [IO.File]::Exists($command.Source)) { return $command.Source }
    $locator = Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $vswhere = if ($locator) { $locator.Source } elseif (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe' }
    if ($vswhere -and [IO.File]::Exists($vswhere)) {
        $candidates = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\Roslyn\vbc.exe')
        if ($LASTEXITCODE -eq 0) {
            foreach ($candidate in $candidates) { if ($candidate -and [IO.File]::Exists($candidate)) { return [IO.Path]::GetFullPath($candidate) } }
        }
    }
    throw 'Visual Studio VB compiler not found. Install the MSBuild tools, put vbc.exe on PATH, or pass -VbcPath / -ExecutablePath explicitly.'
}
