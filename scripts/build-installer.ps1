# Construit les installeurs Windows :
#   dist\SpokaPilote-Setup-<version>.exe        (PC x64 : Intel / AMD)
#   dist\SpokaPilote-Setup-<version>-arm64.exe  (PC ARM64 : Snapdragon, Surface)
param(
    [ValidateSet("x64", "arm64", "all")] [string]$Arch = "all",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Version $version" -ForegroundColor Cyan

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

$archs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$first = $true
foreach ($a in $archs) {
    # 1. Application autonome (interface + CLI) ; les tests ne tournent qu'une fois.
    & "$PSScriptRoot\publish.ps1" -Arch $a -SkipTests:($SkipTests -or -not $first)
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication $a a échoué." }
    $first = $false

    # 2. Inno Setup.
    & $iscc "/DAppVersion=$version" "/DAppArch=$a" /Qp "$root\installer\SpokaPilote.iss"
    if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installeur $a a échoué." }
    $suffix = if ($a -eq "arm64") { "-arm64" } else { "" }
    Write-Host "OK -> $root\dist\SpokaPilote-Setup-$version$suffix.exe" -ForegroundColor Green
}
