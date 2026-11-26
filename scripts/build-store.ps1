# Construit le paquet MSIX de SpokaPilote (Microsoft Store ou installation hors Store) dans dist-store\ :
#   .\scripts\build-store.ps1                  -> SpokaPilote-<version>-x64.msix, NON signé : c'est le Store qui le signe
#   .\scripts\build-store.ps1 -Arch arm64      -> SpokaPilote-<version>-arm64.msix
#   .\scripts\build-store.ps1 -Arch all        -> les deux + SpokaPilote-<version>.msixbundle (un seul fichier à soumettre)
#   .\scripts\build-store.ps1 -TestSign        -> signé avec un certificat auto-signé local (test en chargement latéral)
#   .\scripts\build-store.ps1 -Sign            -> signé avec VOTRE certificat (configuration : docs\SIGNATURE.md),
#                                                 pour une distribution hors Store ; l'éditeur du paquet = sujet du certificat
#   .\scripts\build-store.ps1 -Screenshots     -> génère aussi les captures de la fiche Store (dist-store\captures)
# Aucun SDK Windows requis : makeappx / signtool viennent du paquet NuGet Microsoft.Windows.SDK.BuildTools.
param(
    [ValidateSet("x64", "arm64", "all")] [string]$Arch = "x64",
    [string]$IdentityName = "Spoka.SpokaPilote",
    [string]$Publisher = "CN=Spoka",
    [string]$PublisherDisplayName = "Spoka",
    [switch]$TestSign,
    [switch]$Sign,
    [switch]$Screenshots,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. "$PSScriptRoot\signing.ps1"

if ($TestSign -and $Sign) { throw "Choisissez -TestSign (certificat de test) OU -Sign (votre certificat)." }

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$msixVersion = "$version.0" # le 4ᵉ nombre doit rester 0 pour le Store
Write-Host "Version $msixVersion" -ForegroundColor Cyan

# Signature du paquet : l'éditeur déclaré dans le manifeste doit être exactement le sujet du certificat.
$packageSigning = $null
if ($Sign) {
    $packageSigning = Get-SigningConfig
    if (-not $packageSigning) { throw "-Sign : aucune signature configurée (voir docs\SIGNATURE.md)." }
    if ($packageSigning.Subject) {
        if ($PSBoundParameters.ContainsKey("Publisher") -and $Publisher -ne $packageSigning.Subject) {
            throw "-Publisher « $Publisher » différent du sujet du certificat « $($packageSigning.Subject) » : Windows refuserait le paquet."
        }
        $Publisher = $packageSigning.Subject
    }
    elseif (-not $PSBoundParameters.ContainsKey("Publisher")) {
        throw "Avec Azure Artifact Signing, indiquez -Publisher avec le sujet exact du certificat (portail Azure > profil de certificat)."
    }
    Write-Host "Signature : $($packageSigning.Description) ; éditeur du paquet : $Publisher" -ForegroundColor Cyan
}
elseif ($TestSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "SpokaPilote (test)" `
            -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        Write-Host "Certificat de test créé : $($cert.Thumbprint) (Cert:\CurrentUser\My, à supprimer après vos essais)" -ForegroundColor Yellow
    }
    $packageSigning = [pscustomobject]@{
        Mode = "Test"; Description = "certificat de test « $Publisher »"; SignTool = (Get-SdkBuildTools).SignTool
        Timestamp = $null; SignArgs = @("/fd", "SHA256", "/sha1", $cert.Thumbprint); Subject = $Publisher; Secret = $null
    }
}

$tools = Get-SdkBuildTools
$distStore = Join-Path $root "dist-store"
New-Item -ItemType Directory -Force $distStore | Out-Null
$archs = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$packages = @()
$first = $true
foreach ($a in $archs) {
    # 1. Application autonome (interface + CLI) ; ses exe/dll sont signés si une signature est configurée.
    & "$PSScriptRoot\publish.ps1" -Arch $a -SkipTests:($SkipTests -or -not $first)
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication $a a échoué." }
    $first = $false

    $layout = Join-Path $distStore "layout-$a"
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
    New-Item -ItemType Directory -Force $layout | Out-Null
    Copy-Item "$root\dist\app-$a\*" $layout -Recurse
    foreach ($exe in "SpokaPilote.App.exe", "spokapilote.exe") {
        if (-not (Test-Path (Join-Path $layout $exe))) { throw "$exe absent du paquet $a (déclaré dans AppxManifest.xml)." }
    }

    # 2. Manifeste et visuels.
    Copy-Item (Join-Path $root "packaging\Assets") (Join-Path $layout "Assets") -Recurse -Force
    $manifest = Get-Content (Join-Path $root "packaging\AppxManifest.xml") -Raw -Encoding UTF8
    $manifest = $manifest.Replace("__IDENTITY_NAME__", $IdentityName).Replace("__PUBLISHER__", [System.Security.SecurityElement]::Escape($Publisher)).
        Replace("__PUBLISHER_DISPLAY_NAME__", [System.Security.SecurityElement]::Escape($PublisherDisplayName)).Replace("__VERSION__", $msixVersion).Replace("__ARCH__", $a)
    [System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

    # 3. Paquet.
    $msix = Join-Path $distStore "SpokaPilote-$version-$a.msix"
    & $tools.MakeAppx pack /d $layout /p $msix /o /nv | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx a échoué ($a)." }
    Remove-Item $layout -Recurse -Force
    $packages += $msix
}

# 4. Lot multi-architecture (.msixbundle) : un seul fichier pour le Store, Windows choisit la bonne architecture.
if ($Arch -eq "all") {
    $bundleDir = Join-Path $distStore "bundle"
    if (Test-Path $bundleDir) { Remove-Item $bundleDir -Recurse -Force }
    New-Item -ItemType Directory -Force $bundleDir | Out-Null
    $packages | ForEach-Object { Copy-Item $_ $bundleDir }
    $bundle = Join-Path $distStore "SpokaPilote-$version.msixbundle"
    & $tools.MakeAppx bundle /d $bundleDir /p $bundle /bv $msixVersion /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle a échoué." }
    Remove-Item $bundleDir -Recurse -Force
    $packages += $bundle
}

# 5. Signature facultative des paquets (jamais pour une soumission au Store : il signe lui-même).
if ($packageSigning) {
    Invoke-CodeSign $packageSigning $packages
    foreach ($p in $packages) { Test-CodeSignature $packageSigning $p }
    if ($TestSign) {
        $cer = Join-Path $distStore "SpokaPilote-test.cer"
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Write-Host "Test : importez $cer dans « Ordinateur local > Personnes autorisées », puis double-cliquez sur le .msix." -ForegroundColor Yellow
    }
}

if ($Screenshots) { & "$PSScriptRoot\screenshots.ps1" -Output (Join-Path $distStore "captures") }

$state = if ($packageSigning) { "signé : $($packageSigning.Description)" } else { "non signé, pour le Store" }
foreach ($p in $packages) { Write-Host "OK -> $p ($state)" -ForegroundColor Green }
