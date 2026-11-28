# Règle de détection personnalisée (Intune, application Win32 ; utilisable aussi par MECM, script de détection).
# Détecté = SpokaPilote installé (clé de désinstallation Inno Setup) dans une version >= $MinimumVersion ET
# spokapilote.exe présent. Intune : détecté si le script écrit sur la sortie standard et se termine par 0 ; sinon
# (rien écrit, code 1) Intune considère l'application absente et l'installe.
#
# Intune n'accepte pas de paramètres pour un script de détection : modifiez les deux lignes ci-dessous à chaque version
# déployée. Pour SpokaPrint : Detect-SpokaPrint.ps1. Compatible Windows PowerShell 5.1, processus 32 ou 64 bits.

$MinimumVersion = '4.0.0'
$RequireAgentTask = $false   # $true : exige aussi la tâche \SpokaPilote\Agent (l'agent fait partie du déploiement)

$AppId = '{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1'
$Cli = 'spokapilote.exe'
$AgentTask = '\SpokaPilote\Agent'

try {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $base.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$AppId")
    if (-not $key) { exit 1 }
    $version = $null
    if (-not [version]::TryParse([string]$key.GetValue('DisplayVersion'), [ref]$version)) { exit 1 }
    $exe = Join-Path ([string]$key.GetValue('InstallLocation')) $Cli
    $key.Close(); $base.Close()

    if ($version -lt [version]$MinimumVersion) { exit 1 }
    if (-not (Test-Path -LiteralPath $exe)) { exit 1 }
    if ($RequireAgentTask) {
        & (Join-Path $env:windir 'System32\schtasks.exe') /Query /TN $AgentTask *> $null
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
    Write-Output "SpokaPilote $version détecté ($exe)"
    exit 0
}
catch {
    exit 1
}
