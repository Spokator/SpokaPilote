# Génère les manifestes winget (Windows Package Manager) d'une version publiée sur GitHub :
#   packaging\winget\<version>\Spoka.SpokaPilote.yaml                (version)
#   packaging\winget\<version>\Spoka.SpokaPilote.installer.yaml      (installeurs x64 + ARM64 : URL et SHA-256 de la Release)
#   packaging\winget\<version>\Spoka.SpokaPilote.locale.fr-FR.yaml   (description en français, langue par défaut)
#   packaging\winget\<version>\Spoka.SpokaPilote.locale.en-US.yaml   (description en anglais)
# Appelé automatiquement par release.ps1 après la publication. À la main :
#   .\scripts\winget-manifest.ps1                    -> version de Directory.Build.props (la Release doit exister)
#   .\scripts\winget-manifest.ps1 -Version 2.0.1
# Ne soumet RIEN à Microsoft : la soumission (wingetcreate submit) est décrite dans docs\DISTRIBUTION.md.
param(
    [string]$Version,
    [string]$Repo = "Spokator/SpokaPilote",
    [string]$PackageIdentifier = "Spoka.SpokaPilote",
    [switch]$NoValidate
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$manifestVersion = "1.10.0"

if (-not $Version) {
    [xml]$props = Get-Content "$root\Directory.Build.props"
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
$tag = "v$Version"

# 1. Release GitHub : URL, taille et SHA-256 (« digest ») de chaque installeur.
$previous = $ErrorActionPreference
$ErrorActionPreference = "Continue" # le stderr de gh ne doit pas devenir une exception : on lit le code de sortie
$json = $null
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $json = (& gh api "repos/$Repo/releases/tags/$tag" 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) { $json = $null }
}
$ErrorActionPreference = $previous
$release = if ($json) { $json | ConvertFrom-Json } else {
    Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/tags/$tag" -Headers @{ "User-Agent" = "SpokaPilote-build" }
}
if (-not $release) { throw "Release $tag introuvable sur github.com/$Repo." }

function Get-InstallerAsset([string]$Name) {
    $asset = $release.assets | Where-Object name -eq $Name | Select-Object -First 1
    if (-not $asset) { throw "La Release $tag ne contient pas $Name." }
    $sha = $null
    if ($asset.digest -and $asset.digest -match '^sha256:([0-9a-fA-F]{64})$') { $sha = $Matches[1] }
    else {
        # Anciennes Releases sans « digest » : on calcule l'empreinte du fichier publié.
        $local = Join-Path $root "dist\$Name"
        if (-not (Test-Path $local)) {
            $local = Join-Path $env:TEMP "spokapilote-winget-$Name"
            Invoke-WebRequest $asset.browser_download_url -OutFile $local -UseBasicParsing
        }
        $sha = (Get-FileHash $local -Algorithm SHA256).Hash
        if ($asset.size -and (Get-Item $local).Length -ne $asset.size) { throw "$Name : la taille du fichier ne correspond pas à la Release." }
    }
    [pscustomobject]@{ Url = $asset.browser_download_url; Sha256 = $sha.ToUpperInvariant() }
}
$x64 = Get-InstallerAsset "SpokaPilote-Setup-$Version.exe"
$arm64 = Get-InstallerAsset "SpokaPilote-Installer-$Version-arm64.exe"
$releaseDate = ([datetime]$release.published_at).ToUniversalTime().ToString("yyyy-MM-dd")

# ProductCode winget d'un installeur Inno Setup = clé de désinstallation « <AppId>_is1 ».
$iss = Get-Content "$root\installer\SpokaPilote.iss" -Raw -Encoding UTF8
if ($iss -notmatch '(?m)^AppId=\{(\{[0-9A-Fa-f-]{36}\})') { throw "AppId introuvable dans installer\SpokaPilote.iss." }
$productCode = "$($Matches[1])_is1"

# Notes de version : section du CHANGELOG.
$changelog = Get-Content "$root\CHANGELOG.md" -Raw -Encoding UTF8
$notes = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\b.*?\r?\n(.*?)(?=^## |\z)").Groups[1].Value.Trim()

function Q([string]$s) { "'" + $s.Replace("'", "''") + "'" }      # chaîne YAML entre apostrophes
function Block([string]$s) { ($s -split "\r?\n" | ForEach-Object { if ($_) { "  $_" } else { "" } }) -join "`n" }

$id = $PackageIdentifier
$header = "# Généré par scripts/winget-manifest.ps1 — ne pas modifier à la main."
$files = [ordered]@{}

$files["$id.yaml"] = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifestVersion.schema.json

PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: fr-FR
ManifestType: version
ManifestVersion: $manifestVersion
"@

$files["$id.installer.yaml"] = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifestVersion.schema.json

PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
InstallerSwitches:
  Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  SilentWithProgress: /SILENT /SUPPRESSMSGBOXES /NORESTART /SP-
UpgradeBehavior: install
ElevationRequirement: elevatesSelf
Commands:
- spokapilote
ProductCode: $(Q $productCode)
ReleaseDate: $releaseDate
AppsAndFeaturesEntries:
- DisplayName: SpokaPilote
  Publisher: Spoka
  DisplayVersion: $Version
  ProductCode: $(Q $productCode)
InstallationMetadata:
  DefaultInstallLocation: '%ProgramFiles%\SpokaPilote'
Installers:
- Architecture: x64
  InstallerUrl: $($x64.Url)
  InstallerSha256: $($x64.Sha256)
- Architecture: arm64
  InstallerUrl: $($arm64.Url)
  InstallerSha256: $($arm64.Sha256)
ManifestType: installer
ManifestVersion: $manifestVersion
"@

$tags = "pilotes", "drivers", "imprimantes", "printers", "windows-update", "parc-informatique", "deploiement", "administration"
$files["$id.locale.fr-FR.yaml"] = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifestVersion.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: fr-FR
Publisher: Spoka
PublisherUrl: https://github.com/Spokator
PublisherSupportUrl: https://github.com/$Repo/issues
Author: Spokator
PackageName: SpokaPilote
PackageUrl: https://spokator.github.io/SpokaPilote/
License: MIT
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 Spokator
ShortDescription: Les bons pilotes pour tout le poste et les imprimantes d'entreprise, depuis les sources officielles uniquement.
Description: |-
  SpokaPilote inventorie le poste, identifie chaque composant par son identifiant matériel exact et va chercher le pilote
  chez Microsoft (Windows Update, Microsoft Update Catalog), chez le constructeur du PC (Dell, HP, Lenovo) ou chez NVIDIA.
  Chaque pilote est vérifié (domaine officiel en HTTPS, empreinte, signature numérique, compatibilité réelle) avant
  installation, avec point de restauration, sauvegarde et annulation en un clic. SpokaPilote configure aussi les
  imprimantes et copieurs réseau et remonte l'état du parc dans une console centrale auto-hébergée.
  Interface graphique et commande « spokapilote » pour les scripts de déploiement.
Moniker: spokapilote
Tags:
$(($tags | ForEach-Object { "- $_" }) -join "`n")
ReleaseNotes: |-
$(Block $notes)
ReleaseNotesUrl: https://github.com/$Repo/releases/tag/$tag
Documentations:
- DocumentLabel: Documentation
  DocumentUrl: https://spokator.github.io/SpokaPilote/documentation.html
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

$files["$id.locale.en-US.yaml"] = @"
$header
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$manifestVersion.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Spoka
PublisherUrl: https://github.com/Spokator
PublisherSupportUrl: https://github.com/$Repo/issues
Author: Spokator
PackageName: SpokaPilote
PackageUrl: https://github.com/$Repo
License: MIT
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
Copyright: Copyright (c) 2026 Spokator
ShortDescription: Trusted drivers for the whole PC and for business printers, from official sources only.
Description: |-
  SpokaPilote inventories the PC, identifies each device by its exact hardware ID and fetches the driver from Microsoft
  (Windows Update, Microsoft Update Catalog), from the PC manufacturer (Dell, HP, Lenovo) or from NVIDIA. Every driver is
  verified (official HTTPS domain, hash, digital signature, real hardware compatibility) before installation, with a
  restore point, a backup and one-click rollback. It also sets up network printers and copiers and reports the state of
  the fleet to a self-hosted central console. The user interface is in French; a "spokapilote" command is provided for
  deployment scripts.
Tags:
- drivers
- printers
- windows-update
- device-management
- fleet-management
- sysadmin
ReleaseNotesUrl: https://github.com/$Repo/releases/tag/$tag
ManifestType: locale
ManifestVersion: $manifestVersion
"@

$out = Join-Path $root "packaging\winget\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($name in $files.Keys) {
    $text = ($files[$name] -replace "\r\n", "`n").TrimEnd() + "`n"
    [System.IO.File]::WriteAllText((Join-Path $out $name), $text, $utf8)
}
Write-Host "Manifestes winget -> $out" -ForegroundColor Green

# 2. Validation locale (winget validate), si winget est présent.
if (-not $NoValidate -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    $ErrorActionPreference = "Continue"
    $result = & winget validate --manifest $out --disable-interactivity 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    $result | Where-Object { $_.Trim() } | ForEach-Object { Write-Host "  $_" }
    # 0x8A150028 (-1978335192) : validation réussie avec avertissements.
    if ($code -eq -1978335192) { Write-Host "winget validate : avertissements (voir ci-dessus)." -ForegroundColor Yellow }
    elseif ($code -ne 0) { throw "winget validate a signalé une erreur (code $code)." }
    else { Write-Host "winget validate : OK" -ForegroundColor Green }
}
