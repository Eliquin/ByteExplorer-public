# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

<#
.SYNOPSIS
    Builds ByteExplorer's Windows installer: dotnet publish (framework-dependent, win-x64), then Inno Setup's ISCC.

.DESCRIPTION
    Publishes ByteExplorer.csproj to artifacts\publish\win-x64 (a clean folder each time) and compiles
    Tools\Installer\ByteExplorer.iss into artifacts\installer\ByteExplorer-<version>-win-x64-setup.exe.
    Needs the .NET 9 SDK (or newer) and Inno Setup 6. Works offline when the NuGet packages are already restored.
    The installer contains no updater and downloads nothing.

.PARAMETER Version
    The version to stamp on the app and the installer (numbers only, e.g. 1.2.0). Default: <Version> of ByteExplorer.csproj.

.PARAMETER Iscc
    Path to ISCC.exe. Default: ISCC.exe on PATH, else the usual Inno Setup 6 folders.

.PARAMETER SkipPublish
    Use the existing artifacts\publish\win-x64 (e.g. after signing ByteExplorer.exe there).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\Installer\build-installer.ps1
.EXAMPLE
    .\Tools\Installer\build-installer.ps1 -Version 1.1.0 -Iscc 'D:\Tools\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Iscc,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'ByteExplorer.csproj'
$script = Join-Path $PSScriptRoot 'ByteExplorer.iss'
$publishDir = Join-Path $repo 'artifacts\publish\win-x64'
$outputDir = Join-Path $repo 'artifacts\installer'

if (-not $Version) {
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node) { throw "No <Version> in $project; pass -Version." }
    $Version = $node.InnerText.Trim()
}

# The installer's version resource takes numbers only (Inno Setup's VersionInfoVersion).
if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "Version '$Version' must be numbers only, like 1.2.0."
}

function Find-Iscc {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe' }
    if ($env:LOCALAPPDATA) { $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' } # per-user install
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    return $null
}

if (-not $Iscc) { $Iscc = Find-Iscc }
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) {
    throw 'ISCC.exe (Inno Setup 6) was not found. Install Inno Setup 6 or pass -Iscc <path to ISCC.exe>.'
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force # nothing left over from an older build ends up in the installer
    }

    Write-Host "Publishing ByteExplorer $Version ($Configuration, win-x64, framework-dependent)..."
    # VlcWindowsX86Enabled=false: the app runs as an x64 process and loads only libvlc\win-x64; the 32-bit copy would
    # double the installer's size for nothing.
    & dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publishDir "-p:Version=$Version" '-p:VlcWindowsX86Enabled=false'
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

foreach ($required in @('ByteExplorer.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required))) {
        throw "$required is missing from $publishDir."
    }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Write-Host "Compiling the installer with $Iscc..."
& $Iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $outputDir "ByteExplorer-$Version-win-x64-setup.exe"
Write-Host "Done: $setup"
