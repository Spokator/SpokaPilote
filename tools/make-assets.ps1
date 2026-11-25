<#
.SYNOPSIS
  Génère l'icône (.ico multi-tailles) et les visuels MSIX/Store de SpokaPilote.
  Usage : powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$appAssets = Join-Path $root 'src\SpokaPilote.App\Assets'
$pkgAssets = Join-Path $root 'packaging\Assets'
New-Item -ItemType Directory -Force $appAssets, $pkgAssets | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Dessine le logo dans un carré de côté $size, décalé de ($ox, $oy).
function Draw-Logo([System.Drawing.Graphics]$g, [float]$ox, [float]$oy, [float]$size, [bool]$background) {
    $g.SmoothingMode = 'AntiAlias'
    if ($background) {
        $rect = New-Object System.Drawing.RectangleF($ox, $oy, $size, $size)
        $bg = New-RoundedRect $ox $oy $size $size ($size * 0.22)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
            [System.Drawing.Color]::FromArgb(255, 11, 99, 206), [System.Drawing.Color]::FromArgb(255, 20, 184, 166), 45)
        $g.FillPath($brush, $bg)
    }
    # Bouclier
    $s = $size
    $cx = $ox + $s / 2
    $top = $oy + $s * 0.17
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddLine($cx, $top, $ox + $s * 0.78, $oy + $s * 0.28)
    $shield.AddBezier($ox + $s * 0.78, $oy + $s * 0.28, $ox + $s * 0.80, $oy + $s * 0.62, $ox + $s * 0.66, $oy + $s * 0.76, $cx, $oy + $s * 0.85)
    $shield.AddBezier($cx, $oy + $s * 0.85, $ox + $s * 0.34, $oy + $s * 0.76, $ox + $s * 0.20, $oy + $s * 0.62, $ox + $s * 0.22, $oy + $s * 0.28)
    $shield.CloseFigure()
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($white, $shield)
    # Coche
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 11, 99, 206), [float]($s * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        (New-Object System.Drawing.PointF(($ox + $s * 0.36), ($oy + $s * 0.50))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.46), ($oy + $s * 0.61))),
        (New-Object System.Drawing.PointF(($ox + $s * 0.65), ($oy + $s * 0.39)))
    )
    $g.DrawLines($pen, $pts)
}

function Save-Png([int]$w, [int]$h, [string]$path, [bool]$transparent = $false) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $size = [Math]::Min($w, $h)
    if ($w -ne $h) { $size = [Math]::Min($w, $h) * 0.8 }
    Draw-Logo $g (($w - $size) / 2) (($h - $size) / 2) $size $true
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# --- Icône multi-résolution (entrées PNG) ---
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Logo $g 0 0 $sz $true
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$icoPath = Join-Path $appAssets 'SpokaPilote.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Close()

# --- Visuels MSIX / Microsoft Store ---
Save-Png 44 44   (Join-Path $pkgAssets 'Square44x44Logo.png')
Save-Png 150 150 (Join-Path $pkgAssets 'Square150x150Logo.png')
Save-Png 310 150 (Join-Path $pkgAssets 'Wide310x150Logo.png')
Save-Png 50 50   (Join-Path $pkgAssets 'StoreLogo.png')
Save-Png 620 300 (Join-Path $pkgAssets 'SplashScreen.png')
Save-Png 300 300 (Join-Path $pkgAssets 'StoreListing300.png')
Save-Png 256 256 (Join-Path $pkgAssets 'Square44x44Logo.targetsize-256_altform-unplated.png')
Write-Host "Icône : $icoPath"
Write-Host "Visuels : $pkgAssets"
