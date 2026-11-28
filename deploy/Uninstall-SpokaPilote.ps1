<#
.SYNOPSIS
    Désinstalle SpokaPilote ou SpokaPrint en silence, sans laisser de tâche planifiée.

.DESCRIPTION
    1. Supprime les tâches planifiées du produit (\SpokaPilote\Maintenance, \SpokaPilote\Agent, \SpokaPilote\Analyse ;
       \SpokaPrint\Deploiement), puis le dossier du Planificateur s'il est vide. Les désinstalleurs 4.0.0 et antérieurs
       de SpokaPilote ne retiraient pas ces tâches : le script le fait pour eux, avant et après la désinstallation.
    2. Lance le désinstalleur Inno Setup en silence et attend la disparition de la clé de désinstallation.
    3. Avec -RemoveData : supprime %ProgramData%\<Produit> (réglages, jeton, journaux, rapports, cache) ; les sauvegardes
       de pilotes (Backups) sont conservées sauf avec -RemoveBackups. Sans -RemoveData, les données restent (comme une
       désinstallation silencieuse classique) : une réinstallation retrouve sa configuration.

    Idempotent : sur un poste où le produit n'est pas installé, il retire les éventuelles tâches orphelines et
    renvoie 0. Compatible Windows PowerShell 5.1 et PowerShell 7. À exécuter en administrateur ou en SYSTEM.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-SpokaPilote.ps1
.EXAMPLE
    .\Uninstall-SpokaPilote.ps1 -Product SpokaPrint -RemoveData -WhatIf

.NOTES
    Codes de sortie : 0 succès · 3010 succès, des fichiers seront supprimés au redémarrage · 1 échec · 64 paramètres
    invalides.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('SpokaPilote', 'SpokaPrint')][string]$Product = 'SpokaPilote',
    [switch]$RemoveData,
    [switch]$RemoveBackups,
    [ValidateRange(30, 3600)][int]$TimeoutSeconds = 600,
    [string]$LogDirectory = (Join-Path $env:windir 'Logs\Spoka')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ExitOk = 0; $ExitError = 1; $ExitUsage = 64; $ExitReboot = 3010

$Products = @{
    SpokaPilote = @{ AppId = '{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1'; Cli = 'spokapilote.exe'; Gui = 'SpokaPilote.App.exe'
        TaskFolder = '\SpokaPilote'; Tasks = @('\SpokaPilote\Maintenance', '\SpokaPilote\Agent', '\SpokaPilote\Analyse') }
    SpokaPrint  = @{ AppId = '{37FF2434-ECEF-44C5-983A-AA4DBB7B38FB}_is1'; Cli = 'spokaprint.exe'; Gui = 'SpokaPrint.App.exe'
        TaskFolder = '\SpokaPrint'; Tasks = @('\SpokaPrint\Deploiement') }
}
$P = $Products[$Product]
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:LogFile = $null

function Write-Log([string]$Message, [string]$Level = 'INFO') {
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    if ($script:LogFile) { try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 } catch { } }
}

function Exit-Script([int]$Code, [string]$Message) {
    if ($Message) { Write-Log $Message $(if ($Code -in $ExitOk, $ExitReboot) { 'INFO' } else { 'ERROR' }) }
    Write-Log "Fin — code de sortie $Code"
    exit $Code
}

if (-not $WhatIfPreference) {
    try {
        New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
        $script:LogFile = Join-Path $LogDirectory "Uninstall-$Product-$stamp.log"
    }
    catch { Write-Host "Journal impossible dans $LogDirectory : $($_.Exception.Message)" }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Log "=== Uninstall-$Product — $env:COMPUTERNAME — $($identity.Name)$(if ($WhatIfPreference) { ' — SIMULATION (-WhatIf)' }) ==="
if (-not $isAdmin -and -not $WhatIfPreference) { Exit-Script $ExitUsage "Droits administrateur requis (console administrateur, SYSTEM) ; -WhatIf pour une simulation." }
if ($RemoveBackups -and -not $RemoveData) { Exit-Script $ExitUsage "-RemoveBackups s'utilise avec -RemoveData." }

function Get-InstalledProduct {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $base.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$($P.AppId)")
    if (-not $key) { return $null }
    try {
        [pscustomobject]@{
            Version         = [string]$key.GetValue('DisplayVersion')
            InstallLocation = ([string]$key.GetValue('InstallLocation')).TrimEnd('\')
            Uninstaller     = ([string]$key.GetValue('UninstallString')).Trim('"')
        }
    }
    finally { $key.Close(); $base.Close() }
}

$schtasks = Join-Path $env:windir 'System32\schtasks.exe'

function Test-PendingRename([string]$Folder) {
    $pending = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)
    if (-not $pending -or -not $Folder) { return $false }
    return [bool](@($pending.PendingFileRenameOperations) | Where-Object { $_ -and $_.IndexOf($Folder, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Invoke-SchTasks([string[]]$Arguments) {
    # Erreurs de schtasks (« tâche introuvable ») : code de sortie seulement. Windows PowerShell 5.1 transforme sinon
    # la sortie d'erreur redirigée en erreur bloquante (ErrorActionPreference = Stop).
    $ErrorActionPreference = 'Continue'
    & $schtasks @Arguments 2>&1 | Out-Null
    return $LASTEXITCODE
}

function Test-Task([string]$Name) { return ((Invoke-SchTasks @('/Query', '/TN', $Name)) -eq 0) }

function Remove-ProductTasks {
    $left = 0
    foreach ($task in $P.Tasks) {
        if (-not (Test-Task $task)) { continue }
        if ($PSCmdlet.ShouldProcess($task, 'Supprimer la tâche planifiée')) {
            $code = Invoke-SchTasks @('/Delete', '/TN', $task, '/F')
            if ($code -eq 0) { Write-Log "Tâche $task supprimée." } else { Write-Log "Tâche $task : suppression refusée (code $code)." 'WARN'; $left++ }
        }
    }
    # schtasks ne supprime pas les dossiers : API COM du Planificateur, seulement si le dossier est vide.
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $null
        try { $folder = $service.GetFolder($P.TaskFolder) } catch { }
        if ($folder) {
            if ($folder.GetTasks(1).Count -eq 0 -and $folder.GetFolders(0).Count -eq 0) {
                if ($PSCmdlet.ShouldProcess($P.TaskFolder, 'Supprimer le dossier vide du Planificateur')) {
                    $service.GetFolder('\').DeleteFolder($P.TaskFolder.TrimStart('\'), 0)
                    Write-Log "Dossier $($P.TaskFolder) du Planificateur supprimé."
                }
            }
            else { Write-Log "Dossier $($P.TaskFolder) du Planificateur conservé : il contient d'autres tâches." 'WARN' }
        }
    }
    catch { Write-Log "Dossier du Planificateur : $($_.Exception.Message)" 'WARN' }
    return $left
}

try {
    $installed = Get-InstalledProduct
    Write-Log "Installé : $(if ($installed) { "$Product $($installed.Version) ($($installed.InstallLocation))" } else { 'non' })"

    $left = Remove-ProductTasks   # avant : la tâche ne doit pas se lancer pendant la désinstallation
    $result = $ExitOk

    if ($installed) {
        # Processus du produit encore ouverts (l'installeur les ferme aussi, CloseApplications=force).
        foreach ($exe in @($P.Cli, $P.Gui)) {
            Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue | ForEach-Object {
                if ($PSCmdlet.ShouldProcess("$($_.Name) ($($_.Id))", 'Arrêter le processus')) { $_ | Stop-Process -Force -ErrorAction SilentlyContinue }
            }
        }
        $uninstaller = $installed.Uninstaller
        if (-not $uninstaller -or -not (Test-Path -LiteralPath $uninstaller)) {
            $uninstaller = Join-Path $installed.InstallLocation 'unins000.exe'
        }
        if (-not (Test-Path -LiteralPath $uninstaller)) { Exit-Script $ExitError "Désinstalleur introuvable ($uninstaller)." }
        $uninstallLog = if ($script:LogFile) { Join-Path $LogDirectory "Uninstaller-$Product-$stamp.log" } else { $null }
        $uninstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        if ($uninstallLog) { $uninstallArgs += "/LOG=`"$uninstallLog`"" }
        if ($PSCmdlet.ShouldProcess("$uninstaller $($uninstallArgs -join ' ')", "Désinstaller $Product $($installed.Version)")) {
            Write-Log "Désinstallation : $uninstaller $($uninstallArgs -join ' ')"
            $proc = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArgs -Wait -PassThru -WindowStyle Hidden
            Write-Log "Désinstalleur terminé : code $($proc.ExitCode)"
            # Le désinstalleur Inno Setup se relance depuis un dossier temporaire : on attend la fin réelle.
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-InstalledProduct) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
            if (Get-InstalledProduct) { Exit-Script $ExitError "La clé de désinstallation est toujours présente après $TimeoutSeconds s (voir $uninstallLog)." }
            Write-Log "$Product désinstallé."
            if ((Test-Path -LiteralPath (Join-Path $installed.InstallLocation $P.Cli)) -or (Test-PendingRename $installed.InstallLocation)) {
                Write-Log "Des fichiers de $($installed.InstallLocation) seront supprimés au prochain redémarrage." 'WARN'
                $result = $ExitReboot
            }
        }
    }
    else { Write-Log "$Product n'est pas installé : rien à désinstaller." }

    $left += Remove-ProductTasks   # après : filet de sécurité (tâche recréée pendant la désinstallation)
    if ($left -gt 0) { $result = $ExitError }

    $data = Join-Path $env:ProgramData $Product
    # Jeton d'enrôlement (secret) : supprimé même sans -RemoveData (les désinstalleurs 4.0.1 et ultérieurs le font aussi).
    $tokenFile = Join-Path $data 'server-token.dat'
    if ((Test-Path -LiteralPath $tokenFile) -and $PSCmdlet.ShouldProcess($tokenFile, 'Supprimer le jeton d''enrôlement')) {
        Remove-Item -LiteralPath $tokenFile -Force
        Write-Log "Jeton d'enrôlement supprimé : $tokenFile."
    }

    if ($RemoveData) {
        if (Test-Path -LiteralPath $data) {
            $items = Get-ChildItem -LiteralPath $data -Force | Where-Object { $RemoveBackups -or $_.Name -ne 'Backups' }
            foreach ($item in $items) {
                # Mount : une image Windows encore montée serait endommagée ; jamais vidé, supprimé seulement s'il est vide.
                if ($item.Name -eq 'Mount' -and $item.PSIsContainer -and @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue).Count -gt 0) {
                    Write-Log "$($item.FullName) conservé : une image Windows y est montée (démontez-la : spokapilote image cleanup ou dism /Cleanup-Mountpoints)." 'WARN'
                    continue
                }
                if ($PSCmdlet.ShouldProcess($item.FullName, 'Supprimer')) { Remove-Item -LiteralPath $item.FullName -Recurse -Force }
            }
            if ((Get-ChildItem -LiteralPath $data -Force -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0 -and
                $PSCmdlet.ShouldProcess($data, 'Supprimer le dossier vide')) { Remove-Item -LiteralPath $data -Force }
            Write-Log "Données supprimées : $data$(if (-not $RemoveBackups) { ' (sauvegardes de pilotes conservées dans Backups)' })."
        }
    }

    Exit-Script $result $(if ($result -eq $ExitError) { "Des tâches planifiées n'ont pas pu être supprimées." } else { "$Product : désinstallation terminée." })
}
catch {
    Exit-Script $ExitError "Erreur : $($_.Exception.Message)"
}
