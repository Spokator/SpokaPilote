# Construit les installeurs Windows :
#   dist\SpokaPilote-Setup-<version>.exe        (PC x64 : Intel / AMD)
#   dist\SpokaPilote-Installer-<version>-arm64.exe  (PC ARM64 : Snapdragon, Surface)
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

# Signature facultative : rien ne change si aucune configuration n'est présente (voir docs\SIGNATURE.md).
. "$PSScriptRoot\signing.ps1"
$signing = Get-SigningConfig
if ($signing) { Write-Host "Signature : $($signing.Description)" -ForegroundColor Cyan }
else { Write-Host "Signature : non configurée (installeurs non signés, voir docs\SIGNATURE.md)" -ForegroundColor DarkGray }

$archs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$first = $true
foreach ($a in $archs) {
    # 1. Application autonome (interface + CLI) ; les tests ne tournent qu'une fois.
    & "$PSScriptRoot\publish.ps1" -Arch $a -SkipTests:($SkipTests -or -not $first)
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication $a a échoué." }
    $first = $false

    # 2. Inno Setup (l'installeur et son désinstalleur sont signés si une signature est configurée).
    $isccArgs = @("/DAppVersion=$version", "/DAppArch=$a", "/Qp")
    if ($signing) { $isccArgs += @("/DSign=1", "/Sspokasign=$(Get-InnoSignCommand $signing)") }
    # Le stderr d'ISCC ne doit pas devenir une exception (on lit le code de sortie) ; un mot de passe éventuel est masqué.
    $ErrorActionPreference = "Continue"
    & $iscc @isccArgs "$root\installer\SpokaPilote.iss" 2>&1 | ForEach-Object { if ($signing) { Hide-SigningSecret $signing "$_" } else { "$_" } }
    $isccExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($isccExit -ne 0) { throw "La compilation de l'installeur $a a échoué." }
    $suffix = if ($a -eq "arm64") { "-arm64" } else { "" }
    $kind = if ($a -eq "arm64") { "Installer" } else { "Setup" }
    $setup = "$root\dist\SpokaPilote-$kind-$version$suffix.exe"
    if ($signing) { Test-CodeSignature $signing $setup }
    $state = if ($signing) { "signé" } else { "non signé" }
    Write-Host "OK -> $setup ($state)" -ForegroundColor Green
}
