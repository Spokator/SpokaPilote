# Construit le paquet Microsoft Store : dist-store\SpokaPilote-<version>.msix (non signé : le Store le signe).
#   .\scripts\build-store.ps1                -> paquet à soumettre dans Partner Center
#   .\scripts\build-store.ps1 -TestSign      -> paquet signé avec un certificat auto-signé local (test en chargement latéral)
# Aucun SDK Windows requis : makeappx / signtool viennent du paquet NuGet Microsoft.Windows.SDK.BuildTools.
param(
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [string]$IdentityName = "Spoka.SpokaPilote",
    [string]$Publisher = "CN=Spoka",
    [string]$PublisherDisplayName = "Spoka",
    [switch]$TestSign,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$msixVersion = "$version.0" # le 4ᵉ nombre doit rester 0 pour le Store
Write-Host "Version $msixVersion" -ForegroundColor Cyan

# 1. Application autonome (interface + CLI).
& "$PSScriptRoot\publish.ps1" -Arch $Arch -SkipTests:$SkipTests
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication a échoué." }

$distStore = Join-Path $root "dist-store"
$layout = Join-Path $distStore "layout-$Arch"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item "$root\dist\app\*" $layout -Recurse

# 2. Manifeste et visuels.
Copy-Item (Join-Path $root "packaging\Assets") (Join-Path $layout "Assets") -Recurse -Force
$manifest = Get-Content (Join-Path $root "packaging\AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest.Replace("__IDENTITY_NAME__", $IdentityName).Replace("__PUBLISHER__", $Publisher).
    Replace("__PUBLISHER_DISPLAY_NAME__", $PublisherDisplayName).Replace("__VERSION__", $msixVersion).Replace("__ARCH__", $Arch)
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# 3. makeappx / signtool (paquet NuGet officiel Microsoft.Windows.SDK.BuildTools).
$tools = Join-Path $root "packaging\deps\sdk-buildtools"
$makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
if (-not $makeappx) {
    Write-Host "Téléchargement de Microsoft.Windows.SDK.BuildTools (nuget.org)…"
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json").versions |
        Where-Object { $_ -notmatch '-' }
    $v = $versions[-1]
    New-Item -ItemType Directory -Force $tools | Out-Null
    $nupkg = Join-Path $tools "buildtools.$v.nupkg"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$v/microsoft.windows.sdk.buildtools.$v.nupkg" -OutFile $nupkg -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $tools)
    Remove-Item $nupkg
    $makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
}
$signtool = Join-Path $makeappx.DirectoryName "signtool.exe"

$msix = Join-Path $distStore "SpokaPilote-$version.msix"
& $makeappx.FullName pack /d $layout /p $msix /o /nv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx a échoué." }

if ($TestSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "SpokaPilote (test)" `
            -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    $cer = Join-Path $distStore "SpokaPilote-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool a échoué." }
    Write-Host "Test : importez $cer dans « Ordinateur local > Personnes autorisées », puis double-cliquez sur le .msix." -ForegroundColor Yellow
}

Write-Host "OK -> $msix" -ForegroundColor Green
