# Construit l'installeur Windows : dist\SpokaPilote-Setup-<version>.exe
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Version $version" -ForegroundColor Cyan

# 1. Application autonome (interface + CLI).
& "$PSScriptRoot\publish.ps1" -SkipTests:$SkipTests
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication a échoué." }

# 2. Inno Setup.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Installation d'Inno Setup…"
    winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
}

& $iscc "/DAppVersion=$version" /Qp "$root\installer\SpokaPilote.iss"
if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installeur a échoué." }

Write-Host "OK -> $root\dist\SpokaPilote-Setup-$version.exe" -ForegroundColor Green
