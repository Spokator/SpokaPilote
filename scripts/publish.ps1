# Publie SpokaPilote (interface + ligne de commande) en autonome dans .\dist\app (aucune installation de .NET requise).
param(
    [string]$Configuration = "Release",
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [switch]$SkipTests
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

$out = Join-Path $root "dist\app"
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
$cliVersion = & (Join-Path $out "spokapilote.exe") version
if ($LASTEXITCODE -ne 0) { throw "spokapilote.exe ne démarre pas." }
$gui = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $out "SpokaPilote.App.exe"))
if ($gui.InternalName -notlike "SpokaPilote.App*") { throw "SpokaPilote.App.exe n'est pas l'interface graphique ($($gui.InternalName))." }
Write-Host "OK -> $out (CLI $cliVersion)" -ForegroundColor Green
