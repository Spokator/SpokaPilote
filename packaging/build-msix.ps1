<#
.SYNOPSIS
  Construit SpokaPilote : paquet MSIX (Microsoft Store) et version portable (.zip pour clé USB).

.EXAMPLE
  # Paquet de test signé localement (installable par double-clic sur CE poste) :
  .\packaging\build-msix.ps1 -SignForTest

.EXAMPLE
  # Paquet pour le Microsoft Store (non signé : Partner Center le signe gratuitement) :
  .\packaging\build-msix.ps1 -IdentityName "12345Editeur.SpokaPilote" -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "Pierre C."
#>
param(
    [string]$Version = "0.1.0.0",
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [string]$IdentityName = "SpokaPilote.SpokaPilote",
    [string]$Publisher = "CN=SpokaPilote Dev",
    [string]$PublisherDisplayName = "SpokaPilote",
    [switch]$SignForTest
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "out"
$layout = Join-Path $out "msix-layout-$Arch"
$rid = "win-$Arch"

Write-Host "== Publication ($rid, autonome)" -ForegroundColor Cyan
Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null
$common = @("-c", "Release", "-r", $rid, "--self-contained", "true", "-p:Version=$($Version.Substring(0, $Version.LastIndexOf('.')))", "-o", $layout, "-nologo", "-v", "q")
dotnet publish (Join-Path $root "src\SpokaPilote.App\SpokaPilote.App.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de l'application" }
dotnet publish (Join-Path $root "src\SpokaPilote.Cli\SpokaPilote.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Échec de la publication de la CLI" }
Get-ChildItem $layout -Filter *.pdb | Remove-Item

# --- Version portable (clé USB) ---
$zip = Join-Path $out "SpokaPilote-$Version-portable-$Arch.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $layout "*") -DestinationPath $zip
Write-Host "Portable : $zip" -ForegroundColor Green

# --- Manifeste et visuels ---
Copy-Item (Join-Path $root "packaging\Assets") (Join-Path $layout "Assets") -Recurse -Force
$manifest = Get-Content (Join-Path $root "packaging\AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest.Replace("__IDENTITY_NAME__", $IdentityName).Replace("__PUBLISHER__", $Publisher).
    Replace("__PUBLISHER_DISPLAY_NAME__", $PublisherDisplayName).Replace("__VERSION__", $Version).Replace("__ARCH__", $Arch)
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# --- Outils du SDK Windows (makeappx / signtool) via le paquet NuGet officiel Microsoft.Windows.SDK.BuildTools ---
$tools = Join-Path $out "sdk-buildtools"
$makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
if (-not $makeappx) {
    Write-Host "== Téléchargement de Microsoft.Windows.SDK.BuildTools (nuget.org)" -ForegroundColor Cyan
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json").versions |
        Where-Object { $_ -notmatch '-' }
    $v = $versions[-1]
    $nupkg = Join-Path $out "buildtools.$v.nupkg"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$v/microsoft.windows.sdk.buildtools.$v.nupkg" -OutFile $nupkg -UseBasicParsing
    $sig = Get-AuthenticodeSignature $nupkg
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $tools)
    $makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
}
$signtool = Join-Path $makeappx.DirectoryName "signtool.exe"

$msix = Join-Path $out "SpokaPilote_$($Version)_$Arch.msix"
Write-Host "== Création du paquet MSIX" -ForegroundColor Cyan
& $makeappx.FullName pack /d $layout /p $msix /o /nv | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx a échoué" }

if ($SignForTest) {
    Write-Host "== Signature de test (certificat auto-signé, ce poste uniquement)" -ForegroundColor Cyan
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "SpokaPilote (test)" `
            -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    $cer = Join-Path $out "SpokaPilote-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool a échoué" }
    Write-Host "Pour installer le paquet de test : importez $cer dans « Personnes autorisées » (Ordinateur local), puis double-cliquez sur le .msix." -ForegroundColor Yellow
}

Write-Host "MSIX : $msix" -ForegroundColor Green
