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
Write-Host "OK -> $out" -ForegroundColor Green
