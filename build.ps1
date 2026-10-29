<#
.SYNOPSIS
    Builds AutoSubtitleGenerator, checking out the Subtitle Edit source it builds on first.

.DESCRIPTION
    AutoSubtitleGenerator calls Subtitle Edit's own code, so it is built against the Subtitle
    Edit source at the same version as the installed Subtitle Edit. Bump $SeTag after
    upgrading Subtitle Edit and run this again.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$SeTag = 'v5.4.0-beta8'
$SeRepository = 'https://github.com/SubtitleEdit/subtitleedit.git'
$SeSource = Join-Path $PSScriptRoot 'subtitleedit'

function Invoke-Native {
    param([string] $What, [scriptblock] $Command)

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit code $LASTEXITCODE)."
    }
}

$currentTag = if (Test-Path (Join-Path $SeSource '.git')) { git -C $SeSource describe --tags --exact-match 2>$null }
if ($currentTag -ne $SeTag) {
    if (Test-Path $SeSource) {
        Remove-Item $SeSource -Recurse -Force
    }

    Write-Host "Checking out Subtitle Edit $SeTag..."
    Invoke-Native 'git clone' { git -c advice.detachedHead=false clone --quiet --depth 1 --branch $SeTag $SeRepository $SeSource }
}

Invoke-Native 'dotnet build' { dotnet build (Join-Path $PSScriptRoot 'AutoSubtitleGenerator.csproj') -c Release -nologo }
