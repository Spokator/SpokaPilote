# Captures d'écran de SpokaPilote, générées par l'application elle-même (une image par onglet, après une analyse) :
#   .\scripts\screenshots.ps1                 -> dist-store\captures\ : captures brutes + versions « Store » (1600 × 900)
#   .\scripts\screenshots.ps1 -Site           -> met aussi à jour les captures du site web (site\assets\captures)
#   .\scripts\screenshots.ps1 -NoScan         -> sans analyse du poste (plus rapide, écrans vides)
# Le nom de l'ordinateur affiché en tête de l'écran « Ce poste » est remplacé par -Alias (« PC-DEMO ») :
# aucune information personnelle dans des images destinées à être publiées.
param(
    [ValidateSet("x64", "arm64")] [string]$Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { "arm64" } else { "x64" }),
    [string]$Output,
    [string]$Alias = "PC-DEMO",
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
$appArgs = "--screenshot `"$raw`""
if (-not $NoScan) { $appArgs += " --scan" }
Write-Host "Capture des onglets$(if (-not $NoScan) { ' (après analyse du poste, environ 1 minute)' })…"
$ui = Start-Process $exe -ArgumentList $appArgs -PassThru
if (-not $ui.WaitForExit(600000)) { $ui.Kill(); throw "L'application ne s'est pas fermée." }
if ($ui.ExitCode -ne 0) { throw "L'application a rencontré une erreur (code $($ui.ExitCode))." }

# Ordre des onglets de l'interface -> noms de fichiers lisibles.
$names = "poste", "pilotes", "peripheriques", "imprimantes", "depot", "historique", "journal", "parametres"
New-Item -ItemType Directory -Force $Output | Out-Null
Get-ChildItem $Output -Filter *.png -ErrorAction SilentlyContinue | Remove-Item

function Get-Pixel([System.Drawing.Bitmap]$bmp, [int]$x, [int]$y) { $bmp.GetPixel([Math]::Min($x, $bmp.Width - 1), [Math]::Min($y, $bmp.Height - 1)) }

for ($i = 0; $i -lt $names.Count; $i++) {
    $src = Join-Path $raw "onglet-$($i + 1).png"
    if (-not (Test-Path $src)) { throw "Capture manquante : $src" }
    # La fenêtre utilise l'effet Mica (fond transparent dans la capture) : on l'aplatit sur le fond clair de Windows 11.
    $shot = [System.Drawing.Image]::FromFile($src)
    $bmp = New-Object System.Drawing.Bitmap($shot.Width, $shot.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 243))
    $g.DrawImage($shot, 0, 0, $shot.Width, $shot.Height)
    $shot.Dispose()
    if ($i -eq 0) {
        # Titre de l'écran « Ce poste » : « NOM-DU-PC — Fabricant Modèle » -> « PC-DEMO — Fabricant Modèle ».
        $scale = $bmp.Width / 1224.0 # largeur de la fenêtre à 100 %
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $bg = Get-Pixel $bmp ([int](205 * $scale)) ([int](45 * $scale))
        $fg = if ($bg.GetBrightness() -gt 0.5) { [System.Drawing.Color]::FromArgb(255, 27, 27, 27) } else { [System.Drawing.Color]::White }
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($bg)), [int](212 * $scale), [int](24 * $scale), [int]($bmp.Width - 240 * $scale), [int](40 * $scale))
        $cs = Get-CimInstance Win32_ComputerSystem
        $title = "$Alias — $($cs.Manufacturer) $($cs.Model)".Trim()
        $font = New-Object System.Drawing.Font("Segoe UI Semibold", [float](28 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString($title, $font, (New-Object System.Drawing.SolidBrush($fg)), [float](213 * $scale), [float](25 * $scale))
    }
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
