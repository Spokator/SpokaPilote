<#
.SYNOPSIS
    Recette du kit sur une machine JETABLE (Windows Sandbox, machine virtuelle avec point de contrôle) : installation,
    configuration, tâches, mise à jour, désinstallation, et vérification qu'aucune tâche planifiée ne reste.

.DESCRIPTION
    Refuse de s'exécuter si SpokaPilote est déjà installé : ce script installe et désinstalle réellement.
    -InstallerFolder doit contenir les installeurs (SpokaPilote-Setup-<v>.exe) et un fichier SHA256SUMS.

    Étapes : 1) installe -FromVersion avec une console fictive, l'agent et la maintenance ; 2) relance à l'identique
    (idempotence) ; 3) met à jour vers -ToVersion (si indiqué) et vérifie que réglages et tâches sont conservés ;
    4) désinstalle (Uninstall-SpokaPilote.ps1, ou le désinstalleur seul avec -RawUninstall pour contrôler
    installer\SpokaPilote.iss) ; 5) vérifie : plus de clé de désinstallation, plus de tâche \SpokaPilote\*.

    Windows Sandbox : partagez le dossier du kit et celui des installeurs (fichier .wsb, <MappedFolders>), puis dans
    la Sandbox, PowerShell administrateur :
      Set-ExecutionPolicy -Scope Process Bypass
      C:\Kit\deploy\Test-DeploymentKit.ps1 -InstallerFolder C:\Installeurs -FromVersion 4.0.0 -ToVersion 4.0.1

.NOTES
    Code de sortie : 0 si toutes les vérifications passent, 1 sinon.
#>
param(
    [Parameter(Mandatory = $true)][string]$InstallerFolder,
    [Parameter(Mandatory = $true)][string]$FromVersion,
    [string]$ToVersion,
    [switch]$RawUninstall
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$failures = New-Object Collections.Generic.List[string]
$appKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1'

function Check([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Host "  OK   $What" -ForegroundColor Green } else { Write-Host "  ÉCHEC $What" -ForegroundColor Red; $failures.Add($What) }
}
function Test-Task([string]$Name) {
    $ErrorActionPreference = 'Continue'
    & "$env:windir\System32\schtasks.exe" /Query /TN $Name 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}
function Invoke-Kit([string]$Script, [string[]]$Arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here $Script) @Arguments | Out-Host
    $LASTEXITCODE
}

$admin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Console administrateur requise." -ForegroundColor Red; exit 1 }
if (Test-Path $appKey) { Write-Host "SpokaPilote est déjà installé sur ce poste : recette refusée (machine jetable uniquement)." -ForegroundColor Red; exit 1 }

$token = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$tokenFile = Join-Path $env:TEMP 'spoka-test-token.txt'
Set-Content -LiteralPath $tokenFile -Value $token -Encoding ASCII
$common = @('-SourcePath', $InstallerFolder, '-ServerUrl', 'https://spokapilote.exemple.invalid', '-ServerTokenFile', $tokenFile,
    '-RepositoryPath', '\\srv\pilotes', '-EnableAgent', '-EnableMaintenance', '-RandomDelay', '30')

Write-Host "`n1. Installation $FromVersion + configuration + tâches" -ForegroundColor Cyan
Check ((Invoke-Kit 'Install-SpokaPilote.ps1' ($common + @('-Version', $FromVersion))) -eq 0) "Install-SpokaPilote.ps1 -Version $FromVersion : code 0"
Check ((Get-ItemProperty $appKey).DisplayVersion -eq $FromVersion) "version installée $FromVersion"
Check (Test-Path "$env:ProgramFiles\SpokaPilote\spokapilote.exe") 'spokapilote.exe présent'
Check (Test-Task '\SpokaPilote\Agent') 'tâche \SpokaPilote\Agent créée'
Check (Test-Task '\SpokaPilote\Maintenance') 'tâche \SpokaPilote\Maintenance créée'
$settings = Get-Content "$env:ProgramData\SpokaPilote\settings.json" -Raw | ConvertFrom-Json
Check ($settings.serverUrl -eq 'https://spokapilote.exemple.invalid') 'serverUrl écrit'
$logs = Get-ChildItem "$env:windir\Logs\Spoka\*.log" | Get-Content -Raw
Check (-not ($logs -match $token)) 'jeton absent des journaux du kit'
Check ((Invoke-Kit 'Detect-SpokaPilote.ps1' @()) -eq 0) 'Detect-SpokaPilote.ps1 : détecté'

Write-Host "`n2. Relance à l'identique (idempotence)" -ForegroundColor Cyan
$before = (Get-Item "$env:ProgramData\SpokaPilote\settings.json").LastWriteTimeUtc
Check ((Invoke-Kit 'Install-SpokaPilote.ps1' ($common + @('-Version', $FromVersion))) -eq 0) 'relance : code 0'
Check ((Get-Item "$env:ProgramData\SpokaPilote\settings.json").LastWriteTimeUtc -eq $before) 'settings.json non réécrit'

if ($ToVersion) {
    Write-Host "`n3. Mise à jour vers $ToVersion" -ForegroundColor Cyan
    Check ((Invoke-Kit 'Install-SpokaPilote.ps1' @('-SourcePath', $InstallerFolder, '-Version', $ToVersion)) -eq 0) "mise à jour $ToVersion : code 0"
    Check ((Get-ItemProperty $appKey).DisplayVersion -eq $ToVersion) "version installée $ToVersion"
    Check (Test-Task '\SpokaPilote\Agent') 'tâche Agent conservée par la mise à jour'
    Check (Test-Task '\SpokaPilote\Maintenance') 'tâche Maintenance conservée par la mise à jour'
    Check ((Get-Content "$env:ProgramData\SpokaPilote\settings.json" -Raw | ConvertFrom-Json).serverUrl -eq 'https://spokapilote.exemple.invalid') 'réglages conservés'
}

Write-Host "`n4. Désinstallation$(if ($RawUninstall) { ' (désinstalleur seul, sans le script)' })" -ForegroundColor Cyan
if ($RawUninstall) {
    $p = Start-Process "$env:ProgramFiles\SpokaPilote\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    $deadline = (Get-Date).AddMinutes(5); while ((Test-Path $appKey) -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
    Check ($p.ExitCode -eq 0) 'unins000.exe : code 0'
}
else { Check ((Invoke-Kit 'Uninstall-SpokaPilote.ps1' @()) -in 0, 3010) 'Uninstall-SpokaPilote.ps1 : code 0 ou 3010' }

Write-Host "`n5. État final" -ForegroundColor Cyan
Check (-not (Test-Path $appKey)) 'clé de désinstallation supprimée'
Check (-not (Test-Task '\SpokaPilote\Agent')) 'aucune tâche \SpokaPilote\Agent'
Check (-not (Test-Task '\SpokaPilote\Maintenance')) 'aucune tâche \SpokaPilote\Maintenance'
Check ((Invoke-Kit 'Detect-SpokaPilote.ps1' @()) -ne 0) 'Detect-SpokaPilote.ps1 : non détecté'
Remove-Item -LiteralPath $tokenFile -Force -ErrorAction SilentlyContinue

Write-Host ''
if ($failures.Count) { Write-Host "$($failures.Count) vérification(s) en échec." -ForegroundColor Red; exit 1 }
Write-Host 'Toutes les vérifications sont passées.' -ForegroundColor Green
exit 0
