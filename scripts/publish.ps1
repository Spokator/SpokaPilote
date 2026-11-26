# Publie SpokaPilote (interface + ligne de commande) en autonome dans .\dist\app-<arch> (aucune installation de .NET requise).
#   -Portable : produit aussi dist\SpokaPilote-Portable-<version>-<arch>.zip (mode portable, voir docs\MODE-PORTABLE.md).
#   Le nom de l'archive ne contient jamais « Setup » ni « Installer » : les versions 1.0.x prennent le premier « Setup* »
#   d'une Release pour l'installeur (contrat vérifié par les tests : UpdateChecker.PortableAssetName).
param(
    [string]$Configuration = "Release",
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [switch]$SkipTests,
    [switch]$Portable
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipTests) {
    $env:SPOKAPILOTE_HOME = Join-Path $env:TEMP "spokapilote-tests"
    dotnet test "$root\tests\SpokaPilote.Core.Tests" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Les tests ont échoué." }
    Remove-Item Env:\SPOKAPILOTE_HOME
}

$out = Join-Path $root "dist\app-$Arch"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Les deux exécutables partagent le même runtime .NET dans un seul dossier (installeur plus léger).
foreach ($project in "src\SpokaPilote.App\SpokaPilote.App.csproj", "src\SpokaPilote.Cli\SpokaPilote.Cli.csproj") {
    dotnet publish "$root\$project" -c $Configuration -r "win-$Arch" --self-contained true `
        -p:DebugType=none -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "La publication de $project a échoué." }
}
# Contrôles : deux projets publiés dans le même dossier ne doivent jamais s'écraser (Windows ignore la casse).
foreach ($required in "SpokaPilote.App.exe", "SpokaPilote.App.dll", "spokapilote.exe", "spokapilote.dll", "SpokaPilote.Core.dll") {
    if (-not (Test-Path (Join-Path $out $required))) { throw "Fichier attendu absent après publication : $required" }
}
# Signature FACULTATIVE (uniquement si configurée, voir docs\SIGNATURE.md) des seuls fichiers de SpokaPilote :
# le runtime .NET et les bibliothèques Microsoft sont déjà signés par Microsoft.
. "$PSScriptRoot\signing.ps1"
$signing = Get-SigningConfig
if ($signing) {
    Write-Host "Signature : $($signing.Description)" -ForegroundColor Cyan
    $own = @(Get-ChildItem $out -Recurse -File -Include *.exe, *.dll | Where-Object Name -like "SpokaPilote*" | ForEach-Object FullName)
    Invoke-CodeSign $signing $own
    foreach ($f in $own) { Test-CodeSignature $signing $f }
}

# Archive portable : copie de dist\app-<arch> + fichier témoin + dossier Depot vide + LISEZMOI.txt, dans un dossier
# SpokaPilote\ à extraire à la racine de la clé. Préparée à part : dist\app-<arch> (source de l'installeur) ne reçoit
# jamais le fichier témoin, sinon la version installée se croirait portable.
$portableZip = $null
if ($Portable) {
    [xml]$props = Get-Content "$root\Directory.Build.props"
    $version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    $staging = Join-Path $root "dist\portable-$Arch"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $portableRoot = Join-Path $staging "SpokaPilote"
    New-Item -ItemType Directory -Force $portableRoot | Out-Null
    Copy-Item (Join-Path $out "*") $portableRoot -Recurse -Force
    Copy-Item "$root\installer\portable\SpokaPilote.portable" (Join-Path $portableRoot "SpokaPilote.portable")
    # LISEZMOI en UTF-8 avec BOM et fins de ligne Windows (lisible par tous les Bloc-notes).
    $readme = [System.IO.File]::ReadAllText("$root\installer\portable\LISEZMOI.txt") -replace "`r?`n", "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $portableRoot "LISEZMOI.txt"), $readme, (New-Object System.Text.UTF8Encoding($true)))
    New-Item -ItemType Directory -Force (Join-Path $portableRoot "Depot") | Out-Null
    if (Test-Path (Join-Path $portableRoot "Data")) { throw "Le dossier Data ne doit pas être livré dans l'archive portable." }

    $portableZip = Join-Path $root "dist\SpokaPilote-Portable-$version-$Arch.zip"
    if ((Split-Path $portableZip -Leaf) -match "Setup|Installer") { throw "Nom d'archive portable interdit : $portableZip" }
    if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # CreateFromDirectory conserve les dossiers vides (Depot), contrairement à Compress-Archive.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Archive portable -> $portableZip" -ForegroundColor Cyan
}
# Les contrôles d'exécution ne sont possibles que pour l'architecture de ce poste (un PC x64 n'exécute pas l'ARM64).
$hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { "arm64" } else { "x64" }
if ($Arch -ne $hostArch) {
    Write-Host "OK -> $out ($Arch : contrôles d'exécution impossibles sur ce poste $hostArch)" -ForegroundColor Yellow
    return
}
$cliVersion = & (Join-Path $out "spokapilote.exe") version
if ($LASTEXITCODE -ne 0) { throw "spokapilote.exe ne démarre pas." }
$gui = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $out "SpokaPilote.App.exe"))
if ($gui.InternalName -notlike "SpokaPilote.App*") { throw "SpokaPilote.App.exe n'est pas l'interface graphique ($($gui.InternalName))." }
# Contrôle de l'interface : elle doit s'ouvrir, afficher chaque page (navigation à 6 pages : Accueil, Pilotes,
# Périphériques, Historique, Outils, Paramètres) et se fermer sans erreur.
$shots = Join-Path $env:TEMP "spokapilote-smoke"
Remove-Item $shots -Recurse -Force -ErrorAction SilentlyContinue
$ui = Start-Process (Join-Path $out "SpokaPilote.App.exe") -ArgumentList "--screenshot `"$shots`"" -PassThru
if (-not $ui.WaitForExit(120000)) { $ui.Kill(); throw "L'interface ne s'est pas fermée (fenêtre d'erreur ?)." }
if ($ui.ExitCode -ne 0) { throw "L'interface a rencontré une erreur (code $($ui.ExitCode)) : voir %ProgramData%\SpokaPilote\Logs." }
$pages = (Get-ChildItem $shots -Filter "page-*.png").Count
if ($pages -lt 6) { throw "L'interface n'a affiché que $pages page(s) sur 6." }
if ((Get-ChildItem $shots -Filter "assistant-*.png").Count -lt 3) { throw "L'assistant de premier lancement n'a pas été affiché." }
Write-Host "OK -> $out (CLI $cliVersion, interface : $pages pages)" -ForegroundColor Green

# Contrôle de l'archive portable extraite : mode portable reconnu, données rangées à côté de l'exécutable.
if ($portableZip) {
    $check = Join-Path $env:TEMP "spokapilote-portable-check"
    Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::ExtractToDirectory($portableZip, $check)
    foreach ($required in "SpokaPilote.portable", "LISEZMOI.txt", "Depot", "SpokaPilote.App.exe", "spokapilote.exe") {
        if (-not (Test-Path (Join-Path $check "SpokaPilote\$required"))) { throw "Archive portable incomplète : $required absent." }
    }
    $previousHome = $env:SPOKAPILOTE_HOME
    if ($previousHome) { Remove-Item Env:\SPOKAPILOTE_HOME }
    $config = (& (Join-Path $check "SpokaPilote\spokapilote.exe") config show) -join "`n"
    $configExit = $LASTEXITCODE
    if ($previousHome) { $env:SPOKAPILOTE_HOME = $previousHome }
    if ($configExit -ne 0 -or $config -notmatch "Mode portable") { throw "La copie portable ne se reconnaît pas comme telle :`n$config" }
    if (-not (Test-Path (Join-Path $check "SpokaPilote\Data"))) { throw "La copie portable n'a pas créé son dossier Data." }
    Remove-Item $check -Recurse -Force
    Write-Host "OK -> $portableZip (mode portable vérifié)" -ForegroundColor Green
}
