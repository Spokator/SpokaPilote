# Publie une version sur GitHub : construit les installeurs et les archives portables, puis crée la Release vX.Y.Z
# avec les notes du CHANGELOG.
#   .\scripts\release.ps1            -> release de la version indiquée dans Directory.Build.props
#   .\scripts\release.ps1 -Draft     -> brouillon (non visible par la mise à jour automatique)
param(
    [switch]$Draft,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$tag = "v$version"

if (git status --porcelain) { throw "Des modifications ne sont pas commitées : commitez avant de publier." }
# Windows PowerShell 5.1 transforme le stderr d'un exécutable en erreur : on vérifie via le code de sortie.
$previous = $ErrorActionPreference
$ErrorActionPreference = "Continue"
gh release view $tag *> $null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = $previous
if ($exists) { throw "La Release $tag existe déjà : augmentez la version dans Directory.Build.props." }

& "$PSScriptRoot\build-installer.ps1" -SkipTests:$SkipTests
$setup = Join-Path $root "dist\SpokaPilote-Setup-$version.exe"
$setupArm = Join-Path $root "dist\SpokaPilote-Installer-$version-arm64.exe"
foreach ($f in $setup, $setupArm) { if (-not (Test-Path $f)) { throw "Installeur introuvable : $f" } }
# Archives portables (clé USB) : leur nom ne contient ni « Setup » ni « Installer » (les versions 1.0.x prennent le
# premier « Setup* » de la Release pour l'installeur ; les versions actuelles n'acceptent que les .exe).
$portable = Join-Path $root "dist\SpokaPilote-Portable-$version-x64.zip"
$portableArm = Join-Path $root "dist\SpokaPilote-Portable-$version-arm64.zip"
foreach ($f in $portable, $portableArm) {
    if (-not (Test-Path $f)) { throw "Archive portable introuvable : $f" }
    if ((Split-Path $f -Leaf) -match "Setup|Installer") { throw "Nom d'archive portable interdit : $f" }
}

# Notes : la section du CHANGELOG correspondant à la version.
$changelog = Get-Content "$root\CHANGELOG.md" -Raw -Encoding UTF8
$section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))\b.*?\r?\n(.*?)(?=^## |\z)").Groups[1].Value.Trim()
if (-not $section) { throw "Aucune section « ## $version » dans CHANGELOG.md." }
$notes = Join-Path $env:TEMP "spokapilote-notes-$version.md"
"## Nouveautés de la $version`n`n$section`n`n**Installation :** téléchargez ``SpokaPilote-Setup-$version.exe`` (PC Intel/AMD) ou ``SpokaPilote-Installer-$version-arm64.exe`` (PC ARM64 : Snapdragon, Surface) ci-dessous et lancez-le. Pour un parc : ``SpokaPilote-Setup-$version.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART``.`n`n**Mode portable (clé USB, sans installation) :** ``SpokaPilote-Portable-$version-x64.zip`` ou ``SpokaPilote-Portable-$version-arm64.zip``, à extraire à la racine de la clé (voir docs/MODE-PORTABLE.md)." |
    Set-Content $notes -Encoding UTF8

# git et gh écrivent leur progression sur stderr : on lit les codes de sortie plutôt que le flux d'erreur.
$ErrorActionPreference = "Continue"
git push origin HEAD 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { $ErrorActionPreference = "Stop"; throw "git push a échoué." }
$ghArgs = @("release", "create", $tag, $setup, $setupArm, $portable, $portableArm, "--title", "SpokaPilote $version", "--notes-file", $notes)
if ($Draft) { $ghArgs += "--draft" }
gh @ghArgs 2>&1 | ForEach-Object { "$_" }
$ghExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($ghExit -ne 0) { throw "La création de la Release a échoué." }
Write-Host "OK -> https://github.com/Spokator/SpokaPilote/releases/tag/$tag" -ForegroundColor Green
if ((Get-AuthenticodeSignature $setup).Status -eq "NotSigned") {
    Write-Host "Installeurs non signés (SmartScreen peut avertir) : voir docs\SIGNATURE.md pour activer la signature." -ForegroundColor Yellow
}

# Manifestes winget de cette version (URL + SHA-256 des installeurs publiés), commités dans packaging\winget\<version>.
# La soumission à Microsoft reste manuelle : docs\DISTRIBUTION.md.
if (-not $Draft) {
    try {
        & "$PSScriptRoot\winget-manifest.ps1" -Version $version
        $ErrorActionPreference = "Continue" # progression de git sur stderr : on lit les codes de sortie
        git add "packaging/winget/$version"
        git commit -q -m "winget : manifestes $version"
        if ($LASTEXITCODE -eq 0) { git push origin HEAD }
        $ErrorActionPreference = "Stop"
        Write-Host "Soumission winget (quand vous le décidez) : voir docs\DISTRIBUTION.md" -ForegroundColor Cyan
    }
    catch {
        Write-Host "Manifestes winget non générés : $($_.Exception.Message) (relancez .\scripts\winget-manifest.ps1)" -ForegroundColor Yellow
    }
}
