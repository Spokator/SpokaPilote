# Captures d'écran de SpokaPilote, générées par l'application elle-même (une image par page, après une analyse) :
#   .\scripts\screenshots.ps1                 -> dist-store\captures\ : captures brutes + versions « Store » (1600 × 900)
#   .\scripts\screenshots.ps1 -Site           -> met aussi à jour les captures du site web (site\assets\captures)
#   .\scripts\screenshots.ps1 -NoScan         -> sans analyse du poste (plus rapide, écrans vides)
#   .\scripts\screenshots.ps1 -Lang en        -> interface en anglais
# Le nom de l'ordinateur est remplacé par -Alias (« PC-DEMO ») et le numéro de série masqué, par l'application
# elle-même (--alias) : aucune information personnelle dans des images destinées à être publiées.
param(
    [ValidateSet("x64", "arm64")] [string]$Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { "arm64" } else { "x64" }),
    [string]$Output,
    [string]$Alias = "PC-DEMO",
    [ValidateSet("fr", "en")] [string]$Lang = "fr",
    [switch]$NoScan,
    [switch]$Site
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "dist-store\captures" }
Add-Type -AssemblyName System.Drawing

$exe = Join-Path $root "dist\app-$Arch\SpokaPilote.App.exe"
if (-not (Test-Path $exe)) {
    & "$PSScriptRoot\publish.ps1" -Arch $Arch -SkipTests
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "La publication a échoué." }
}

# 1. Captures brutes par l'application (--screenshot), après une analyse réelle du poste.
$raw = Join-Path $env:TEMP "spokapilote-captures"
Remove-Item $raw -Recurse -Force -ErrorAction SilentlyContinue
$appArgs = "--screenshot `"$raw`" --alias `"$Alias`" --lang $Lang"
if (-not $NoScan) { $appArgs += " --scan" }
Write-Host "Capture des pages$(if (-not $NoScan) { ' (après analyse du poste, environ 1 minute)' })…"
$ui = Start-Process $exe -ArgumentList $appArgs -PassThru
if (-not $ui.WaitForExit(600000)) { $ui.Kill(); throw "L'application ne s'est pas fermée." }
if ($ui.ExitCode -ne 0) { throw "L'application a rencontré une erreur (code $($ui.ExitCode))." }

# Pages de la navigation, dans l'ordre (l'application écrit page-N-nom.png) -> noms de fichiers lisibles.
$names = "accueil", "pilotes", "peripheriques", "historique", "outils", "parametres"
New-Item -ItemType Directory -Force $Output | Out-Null
Get-ChildItem $Output -Filter *.png -ErrorAction SilentlyContinue | Remove-Item

for ($i = 0; $i -lt $names.Count; $i++) {
    $src = Join-Path $raw "page-$($i + 1)-$($names[$i]).png"
    if (-not (Test-Path $src)) { throw "Capture manquante : $src" }
    # Fond opaque (couleur de base du thème) déjà posé par l'application ; on aplatit par sécurité sur le fond clair de Windows 11.
    $shot = [System.Drawing.Image]::FromFile($src)
    $bmp = New-Object System.Drawing.Bitmap($shot.Width, $shot.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $g.DrawImage($shot, 0, 0, $shot.Width, $shot.Height)
    $shot.Dispose()
    $g.Dispose()
    $bmp.Save((Join-Path $Output "$($names[$i]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Remove-Item $raw -Recurse -Force

# 2. Versions pour la fiche du Microsoft Store (1600 × 900, au moins 1366 × 768 exigé) : capture sur fond aux couleurs du logo.
foreach ($n in $names) {
    $shot = [System.Drawing.Image]::FromFile((Join-Path $Output "$n.png"))
    $canvasW = 1600; $canvasH = 900
    $canvas = New-Object System.Drawing.Bitmap($canvasW, $canvasH)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
    $rect = New-Object System.Drawing.Rectangle(0, 0, $canvasW, $canvasH)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 11, 99, 206), [System.Drawing.Color]::FromArgb(255, 20, 184, 166), 35)
    $g.FillRectangle($brush, $rect)
    $ratio = [Math]::Min(($canvasW - 120) / $shot.Width, ($canvasH - 90) / $shot.Height)
    $w = [int]($shot.Width * $ratio); $h = [int]($shot.Height * $ratio)
    $x = [int](($canvasW - $w) / 2); $y = [int](($canvasH - $h) / 2)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 0, 0, 0))), $x + 6, $y + 10, $w, $h)
    $g.DrawImage($shot, $x, $y, $w, $h)
    $g.Dispose(); $shot.Dispose()
    $canvas.Save((Join-Path $Output "store-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}
Write-Host "OK -> $Output ($($names.Count) captures + versions store-*.png)" -ForegroundColor Green

# 3. Site web : captures brutes (le site les affiche dans un cadre de fenêtre).
if ($Site) {
    $siteDir = Join-Path $root "site\assets\captures"
    New-Item -ItemType Directory -Force $siteDir | Out-Null
    foreach ($n in $names) { Copy-Item (Join-Path $Output "$n.png") (Join-Path $siteDir "$n.png") -Force }
    Write-Host "OK -> $siteDir" -ForegroundColor Green
}
