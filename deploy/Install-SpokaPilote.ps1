<#
.SYNOPSIS
    Installe (ou met à jour) SpokaPilote ou SpokaPrint en silence, puis configure le poste : settings.json, agent,
    maintenance planifiée. Prévu pour Intune (application Win32), MECM, GPO de démarrage, RMM, PowerShell à distance.

.DESCRIPTION
    1. Choisit l'installeur de l'architecture du poste (x64 ou ARM64) : fichier indiqué, dossier (partage, paquet
       Intune / MECM : par défaut le dossier du script), sinon la Release GitHub officielle.
    2. Copie l'installeur dans un dossier temporaire local, vérifie son SHA-256 (obligatoire : -Sha256, fichier
       <installeur>.sha256 ou SHA256SUMS à côté, ou empreinte publiée par GitHub), puis l'exécute en silence.
       Rien n'est fait si la même version (ou une plus récente) est déjà installée, sauf -Force.
    3. Jeton d'enrôlement (SpokaPilote 4.0.1 et ultérieurs) : confié à la ligne de commande installée, qui l'enregistre
       chiffré dans server-token.dat (SYSTEM et Administrateurs seulement), sans rien modifier s'il est déjà le même. Il
       n'est jamais écrit dans settings.json (lisible par tous les utilisateurs du poste) ; un ancien jeton en clair y est
       retiré. SpokaPrint et SpokaPilote 4.0.0 : écrit dans settings.json, avec un avertissement.
       Fusionne ensuite les réglages demandés dans %ProgramData%\<Produit>\settings.json (clés existantes conservées).
    4. Active si demandé l'agent (actions à distance) et la tâche de maintenance, en compte SYSTEM.

    Idempotent : relancé sur un poste déjà conforme, il ne réinstalle rien et ne réécrit pas un settings.json
    identique ni un jeton déjà enregistré. Le jeton d'enrôlement n'apparaît jamais dans le journal ni sur une ligne de
    commande (il est transmis à spokapilote.exe par son entrée standard).

    Compatible Windows PowerShell 5.1 et PowerShell 7. À exécuter en administrateur ou en SYSTEM (sauf -WhatIf).

.PARAMETER Product
    SpokaPilote (défaut) ou SpokaPrint.
.PARAMETER Version
    Version à installer (ex. 4.0.0). Par défaut : la plus récente trouvée dans -SourcePath, sinon la dernière Release.
.PARAMETER SourcePath
    Installeur (.exe) ou dossier qui contient SpokaPilote-Setup-<v>.exe / SpokaPilote-Installer-<v>-arm64.exe.
    Par défaut : le dossier du script s'il contient un installeur, sinon GitHub.
.PARAMETER Sha256
    Empreinte SHA-256 attendue de l'installeur (sinon : fichier .sha256 / SHA256SUMS, ou empreinte GitHub).
.PARAMETER ServerUrl
    Adresse HTTPS de la console centrale (clé serverUrl).
.PARAMETER ServerToken
    Jeton d'enrôlement. Préférez -ServerTokenFile ou -ServerTokenVariable : la ligne de commande du script est souvent
    journalisée. Enregistré chiffré (server-token.dat), jamais dans settings.json (SpokaPilote 4.0.1 et ultérieurs).
.PARAMETER ServerTokenFile
    Fichier texte contenant le jeton (première ligne), par exemple dans le paquet Intune ou sur un partage lisible par
    les seuls comptes ordinateur.
.PARAMETER ServerTokenVariable
    Nom d'une variable d'environnement qui contient le jeton (variable secrète d'un outil RMM, par exemple).
.PARAMETER RepositoryPath
    Dépôts de pilotes (clé repositoryPaths, remplace la liste existante).
.PARAMETER PolicySource
    Politique de pilotes : chemin, partage UNC ou URL HTTPS (clé policySource).
.PARAMETER UseConsoleCache
    auto, on ou off (clé useConsoleCache).
.PARAMETER SettingsTemplate
    Modèle JSON à fusionner (voir settings.template.json) : appliqué avant les paramètres ci-dessus.
.PARAMETER EnableAgent
    SpokaPilote : crée la tâche \SpokaPilote\Agent (actions à distance de la console), toutes les -AgentEvery minutes.
.PARAMETER EnableMaintenance
    SpokaPilote : tâche \SpokaPilote\Maintenance (-MaintenanceSchedule, -MaintenanceDay, -MaintenanceTime,
    -MaintenanceMode, -RandomDelay, -ReportDirectory). SpokaPrint : tâche \SpokaPrint\Deploiement (-PrinterProfile,
    au démarrage + chaque jour à -MaintenanceTime, -RandomDelay, -ReportDirectory).
.PARAMETER InitialScan
    SpokaPilote : lance une analyse quand ce passage a installé / mis à jour le produit ou modifié settings.json (premier
    rapport : le poste apparaît dans la console, ce que l'agent attend pour exécuter des actions). Rien aux passages
    suivants : un script de démarrage GPO peut garder l'option.
.PARAMETER ForceTasks
    Recrée les tâches planifiées même si elles existent déjà avec les mêmes options (sinon elles sont conservées : une
    tâche recréée perd son historique et le rattrapage d'une exécution manquée).
.PARAMETER ConfigureOnly
    N'installe rien : applique seulement la configuration et les tâches (rotation du jeton, changement de console).
.PARAMETER Force
    Réinstalle même si la version demandée (ou plus récente) est déjà installée.
.PARAMETER DesktopShortcut
    Crée le raccourci sur le Bureau commun (absent par défaut en déploiement).
.PARAMETER LogDirectory
    Dossier des journaux du script et de l'installeur. Défaut : %windir%\Logs\Spoka.
.PARAMETER DataDirectory
    Dossier de données (défaut %ProgramData%\<Produit>). Pour les essais uniquement : avec un autre dossier, les
    tâches planifiées ne peuvent pas être activées.

.EXAMPLE
    # Intune / MECM : installeurs, SHA256SUMS et token.txt dans le paquet, à côté du script.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-SpokaPilote.ps1 -ServerUrl https://spokapilote.corp.fr -ServerTokenFile .\token.txt -EnableAgent -EnableMaintenance

.EXAMPLE
    # Simulation (aucune modification) :
    .\Install-SpokaPilote.ps1 -SourcePath \\srv\logiciels\SpokaPilote -ServerUrl https://spokapilote.corp.fr -WhatIf

.NOTES
    Codes de sortie : 0 succès · 3010 succès, redémarrage requis · 1 échec · 1618 à réessayer plus tard (autre
    exécution en cours, ou Windows attend un redémarrage avant l'installation) · 64 paramètres invalides.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('SpokaPilote', 'SpokaPrint')][string]$Product = 'SpokaPilote',
    [string]$Version,
    [string]$SourcePath,
    [string]$Sha256,
    [string]$ServerUrl,
    [string]$ServerToken,
    [string]$ServerTokenFile,
    [string]$ServerTokenVariable,
    [string[]]$RepositoryPath,
    [string]$PolicySource,
    [ValidateSet('auto', 'on', 'off')][string]$UseConsoleCache,
    [string]$SettingsTemplate,
    [switch]$EnableAgent,
    [ValidateRange(5, 1440)][int]$AgentEvery = 15,
    [switch]$EnableMaintenance,
    [ValidateSet('Weekly', 'Daily', 'AtStartup')][string]$MaintenanceSchedule = 'Weekly',
    [string]$MaintenanceDay = 'lundi',
    [ValidatePattern('^\d{1,2}:\d{2}$')][string]$MaintenanceTime = '12:00',
    [ValidateSet('missing', 'updates', 'scan')][string]$MaintenanceMode = 'missing',
    [ValidateRange(0, 720)][int]$RandomDelay = 120,
    [string]$ReportDirectory,
    [string]$PrinterProfile,
    [switch]$InitialScan,
    [switch]$ForceTasks,
    [switch]$ConfigureOnly,
    [switch]$Force,
    [switch]$DesktopShortcut,
    [string]$Proxy,
    [string]$LogDirectory = (Join-Path $env:windir 'Logs\Spoka'),
    [string]$DataDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest 5.1 : la barre de progression le ralentit fortement

# ------------------------------------------------------------------------------------------------ constantes
$ExitOk = 0; $ExitError = 1; $ExitUsage = 64; $ExitReboot = 3010; $ExitRetry = 1618

$Products = @{
    SpokaPilote = @{ AppId = '{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1'; Cli = 'spokapilote.exe'; Repo = 'Spokator/SpokaPilote'; HomeVariable = 'SPOKAPILOTE_HOME' }
    SpokaPrint  = @{ AppId = '{37FF2434-ECEF-44C5-983A-AA4DBB7B38FB}_is1'; Cli = 'spokaprint.exe'; Repo = 'Spokator/SpokaPrint'; HomeVariable = 'SPOKAPRINT_HOME' }
}
$P = $Products[$Product]

# Clés de settings.json : l'application les lit en camelCase, SENSIBLES À LA CASSE (« ServerUrl » serait ignoré).
$KnownKeys = @('repositoryPaths', 'useWindowsUpdate', 'windowsUpdateBypassWsus', 'useMicrosoftCatalog', 'useOemCatalogs',
    'includeBiosAndFirmware', 'createRestorePoint', 'backupDriversBeforeInstall', 'extraTrustedHosts', 'extraTrustedPublishers',
    'printerCatalogUrl', 'catalogConcurrency', 'serverUrl', 'serverToken', 'policySource', 'useConsoleCache',
    'consoleCacheCatalogs', 'useGpuVendorSources', 'nvidiaBranch')

# Comptes de confiance pour un settings.json existant : SYSTEM, Administrateurs, TrustedInstaller (+ le compte courant).
$TrustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# ------------------------------------------------------------------------------------------------ journal
$script:Secrets = New-Object Collections.Generic.List[string]
$script:LogFile = $null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Write-Log([string]$Message, [string]$Level = 'INFO') {
    foreach ($s in $script:Secrets) { $Message = $Message.Replace($s, '********') }   # filet de sécurité
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
        $script:LogFile = Join-Path $LogDirectory "Install-$Product-$stamp.log"
    }
    catch { Write-Host "Journal impossible dans $LogDirectory : $($_.Exception.Message)" }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Log "=== Install-$Product — $env:COMPUTERNAME — $($identity.Name)$(if ($WhatIfPreference) { ' — SIMULATION (-WhatIf)' }) ==="

# ------------------------------------------------------------------------------------------------ fonctions
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Resolve-InputPath([string]$Path) {
    # Chemin absolu tel quel ; sinon relatif au dossier courant, puis au dossier du script (paquet Intune / MECM).
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    $here = Join-Path (Get-Location).ProviderPath $Path
    if (Test-Path -LiteralPath $here) { return $here }
    Join-Path $PSScriptRoot $Path
}

function Get-NativeArchitecture {
    # Valeur du système, identique pour un processus 32 bits, x64 émulé sur ARM64 ou natif.
    $arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
    switch ($arch) { 'ARM64' { 'arm64' } 'AMD64' { 'x64' } default { throw "Architecture non prise en charge : $arch (x64 ou ARM64 uniquement)." } }
}

function Get-InstalledProduct {
    # Vue 64 bits du registre, même depuis un PowerShell 32 bits (Intune exécute souvent les commandes en 32 bits).
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $base.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$($P.AppId)")
    if (-not $key) { return $null }
    try {
        $location = [string]$key.GetValue('InstallLocation')
        $v = $null
        [void][version]::TryParse([string]$key.GetValue('DisplayVersion'), [ref]$v)
        [pscustomobject]@{
            Version         = $v
            InstallLocation = $location.TrimEnd('\')
            Cli             = Join-Path $location $P.Cli
        }
    }
    finally { $key.Close(); $base.Close() }
}

function Get-InstallerName([string]$Ver, [string]$Arch) {
    if ($Arch -eq 'arm64') { "$Product-Installer-$Ver-arm64.exe" } else { "$Product-Setup-$Ver.exe" }
}

function Find-InstallerInFolder([string]$Folder, [string]$Arch) {
    $pattern = if ($Arch -eq 'arm64') { "^$Product-Installer-(\d+\.\d+\.\d+)-arm64\.exe$" } else { "^$Product-Setup-(\d+\.\d+\.\d+)\.exe$" }
    $found = Get-ChildItem -LiteralPath $Folder -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $pattern } |
        ForEach-Object { [pscustomobject]@{ File = $_.FullName; Version = [version]([regex]::Match($_.Name, $pattern).Groups[1].Value) } }
    if ($Version) { $found = $found | Where-Object { $_.Version -eq [version]$Version } }
    $found | Sort-Object Version -Descending | Select-Object -First 1
}

function Get-Sha256([string]$Path) {
    # .NET plutôt que Get-FileHash, qui ne renvoie rien sous -WhatIf.
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-ExpectedHash([string]$InstallerPath) {
    $name = Split-Path $InstallerPath -Leaf
    $folder = Split-Path $InstallerPath -Parent
    $sidecar = Join-Path $folder "$name.sha256"
    if (Test-Path -LiteralPath $sidecar) {
        $m = [regex]::Match((Get-Content -LiteralPath $sidecar -Raw), '\b[0-9a-fA-F]{64}\b')
        if ($m.Success) { return $m.Value.ToLowerInvariant() }
    }
    $sums = Join-Path $folder 'SHA256SUMS'
    if (Test-Path -LiteralPath $sums) {
        foreach ($line in Get-Content -LiteralPath $sums) {
            $m = [regex]::Match($line, '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$')
            if ($m.Success -and $m.Groups[2].Value -ieq $name) { return $m.Groups[1].Value.ToLowerInvariant() }
        }
    }
    return $null
}

function Invoke-Web([hashtable]$Arguments, [switch]$Api) {
    $Arguments.UseBasicParsing = $true
    $Arguments.Headers = @{ 'User-Agent' = 'SpokaDeploy' }
    if ($Api) { $Arguments.Headers.Accept = 'application/vnd.github+json' }
    if ($Proxy) { $Arguments.Proxy = $Proxy; $Arguments.ProxyUseDefaultCredentials = $true }
    Invoke-WebRequest @Arguments
}

function Resolve-GitHubRelease([string]$Arch) {
    # Version ET empreinte connues : lien direct, sans l'API GitHub (limitée à 60 appels/heure par adresse IP publique :
    # un parc derrière un même accès Internet la dépasse vite).
    if ($Version -and $Sha256) {
        $name = Get-InstallerName $Version $Arch
        return [pscustomobject]@{ Url = "https://github.com/$($P.Repo)/releases/download/v$Version/$name"; Name = $name; Version = [version]$Version; Sha256 = $Sha256.ToLowerInvariant() }
    }
    $api = if ($Version) { "https://api.github.com/repos/$($P.Repo)/releases/tags/v$Version" } else { "https://api.github.com/repos/$($P.Repo)/releases/latest" }
    Write-Log "Release GitHub : $api"
    $release = (Invoke-Web @{ Uri = $api } -Api).Content | ConvertFrom-Json
    $ver = [version]($release.tag_name.TrimStart('v'))
    $name = Get-InstallerName $ver.ToString(3) $Arch
    $asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { throw "Installeur $name absent de la Release $($release.tag_name)." }
    $digest = $null
    if ($asset.PSObject.Properties['digest'] -and "$($asset.digest)" -match '^sha256:([0-9a-fA-F]{64})$') { $digest = $Matches[1].ToLowerInvariant() }
    if ($Sha256) { $digest = $Sha256.ToLowerInvariant() }
    [pscustomobject]@{ Url = $asset.browser_download_url; Name = $name; Version = $ver; Sha256 = $digest }
}

function Read-JsonFile([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json }
    catch {
        # L'application tolère commentaires et virgules finales ; ConvertFrom-Json 5.1 non : on les retire.
        $clean = ($text -split "`r?`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
        $clean = [regex]::Replace($clean, ',(\s*[}\]])', '$1')
        return $clean | ConvertFrom-Json
    }
}

function ConvertTo-SettingsMap($Object, [string]$Origin) {
    $map = [ordered]@{}
    if ($null -eq $Object) { return $map }
    foreach ($prop in $Object.PSObject.Properties) {
        if ($prop.Name.StartsWith('_')) { continue }   # commentaires du modèle
        $known = $KnownKeys | Where-Object { $_ -ieq $prop.Name } | Select-Object -First 1
        $name = $prop.Name
        if ($known -and $known -cne $name) {
            Write-Log "$Origin : clé « $name » renommée « $known » (l'application lit les clés en camelCase, sensibles à la casse)." 'WARN'
            if ($map.Contains($known)) { continue }
            $name = $known
        }
        elseif (-not $known) { Write-Log "$Origin : clé inconnue « $name » conservée (ignorée par l'application)." 'WARN' }
        $map[$name] = $prop.Value
    }
    $map
}

function Set-MapValue($Map, [string]$Key, $Value) {
    if ($null -eq $Value) { if ($Map.Contains($Key)) { $Map.Remove($Key) } } else { $Map[$Key] = $Value }
}

function Test-PendingRename([string]$Folder) {
    # Fichiers en cours d'utilisation remplacés au prochain démarrage (PendingFileRenameOperations) dans ce dossier ?
    $pending = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue)
    if (-not $pending -or -not $Folder) { return $false }
    return [bool](@($pending.PendingFileRenameOperations) | Where-Object { $_ -and $_.IndexOf($Folder, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Test-TaskExists([string]$Name) {
    $ErrorActionPreference = 'Continue'
    & (Join-Path $env:windir 'System32\schtasks.exe') /Query /TN $Name 2>&1 | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Invoke-ProductCli([string]$Cli, [string[]]$Arguments, [string]$What) {
    if (-not $PSCmdlet.ShouldProcess("$Cli $($Arguments -join ' ')", $What)) { return $ExitOk }
    Write-Log "$What : $(Split-Path $Cli -Leaf) $($Arguments -join ' ')"
    $ErrorActionPreference = 'Continue'   # sortie d'erreur de la commande : journalisée, pas bloquante (5.1)
    $output = & $Cli @Arguments 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $output) { if ("$line".Trim()) { Write-Log "    $line" } }
    Write-Log "$What : code $code"
    return $code
}

function Invoke-ProductCliWithSecret([string]$Cli, [string[]]$Arguments, [string]$Secret, [string]$What, [ref]$Output) {
    # Le secret passe par l'entrée standard du processus : jamais sur sa ligne de commande (visible de tous les
    # utilisateurs du poste dans le Gestionnaire des tâches, journalisée par l'audit des processus ou un EDR).
    if (-not $PSCmdlet.ShouldProcess("$Cli $($Arguments -join ' ') (jeton sur l'entrée standard)", $What)) { return $ExitOk }
    Write-Log "$What : $(Split-Path $Cli -Leaf) $($Arguments -join ' ') (jeton sur l'entrée standard)"
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Cli
    $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $psi.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $proc = [Diagnostics.Process]::Start($psi)
    try {
        $stdout = $proc.StandardOutput.ReadToEndAsync()
        $stderr = $proc.StandardError.ReadToEndAsync()
        $proc.StandardInput.WriteLine($Secret)
        $proc.StandardInput.Close()
        $proc.WaitForExit()
        $lines = @(($stdout.Result + "`n" + $stderr.Result) -split "`r?`n" | Where-Object { $_.Trim() })
        foreach ($line in $lines) { Write-Log "    $line" }
        $Output.Value = $lines
        Write-Log "$What : code $($proc.ExitCode)"
        return $proc.ExitCode
    }
    finally { $proc.Dispose() }
}

# ------------------------------------------------------------------------------------------------ paramètres
if (@($ServerToken, $ServerTokenFile, $ServerTokenVariable | Where-Object { $_ }).Count -gt 1) { Exit-Script $ExitUsage "Indiquez une seule source de jeton : -ServerToken, -ServerTokenFile ou -ServerTokenVariable." }
if ($ServerTokenVariable) {
    $ServerToken = [Environment]::GetEnvironmentVariable($ServerTokenVariable)
    if (-not $ServerToken) { Exit-Script $ExitUsage "Variable d'environnement $ServerTokenVariable vide ou absente." }
}
if ($ServerTokenFile) {
    $tokenPath = Resolve-InputPath $ServerTokenFile
    if (-not (Test-Path -LiteralPath $tokenPath)) { Exit-Script $ExitUsage "Fichier de jeton introuvable : $tokenPath" }
    $ServerToken = (Get-Content -LiteralPath $tokenPath -TotalCount 1).Trim()
}
if ($ServerToken) {
    $ServerToken = $ServerToken.Trim()
    $script:Secrets.Add($ServerToken)
    if ($ServerToken -match '\s' -or $ServerToken.Length -lt 16) { Exit-Script $ExitUsage "Jeton d'enrôlement invalide (espaces, ou moins de 16 caractères)." }
}
if ($ServerUrl -and $ServerUrl -notmatch '^https://' -and $ServerUrl -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?(/|$)') {
    Exit-Script $ExitUsage "ServerUrl doit être une adresse HTTPS (HTTP accepté pour localhost seulement) : $ServerUrl"
}
if ($Product -eq 'SpokaPrint' -and ($EnableAgent -or $InitialScan)) { Exit-Script $ExitUsage "-EnableAgent et -InitialScan concernent SpokaPilote (SpokaPrint n'a pas d'agent)." }
if ($Product -eq 'SpokaPrint' -and $EnableMaintenance -and -not $PrinterProfile) { Exit-Script $ExitUsage "SpokaPrint : -EnableMaintenance exige -PrinterProfile (fichier, partage, URL HTTPS ou « console »)." }
if ($Product -eq 'SpokaPrint' -and $RandomDelay -gt 240) { $RandomDelay = 240 }

$defaultData = Join-Path $env:ProgramData $Product
$customData = [bool]$DataDirectory -and ($DataDirectory.TrimEnd('\') -ne $defaultData)
if (-not $DataDirectory) { $DataDirectory = $defaultData }
if ($customData -and ($EnableAgent -or $EnableMaintenance)) { Exit-Script $ExitUsage "-DataDirectory (essais) ne permet pas d'activer les tâches planifiées : elles utilisent toujours $defaultData." }
if (-not $isAdmin -and -not $WhatIfPreference -and -not ($ConfigureOnly -and $customData)) {
    Exit-Script $ExitUsage "Droits administrateur requis (console administrateur, SYSTEM) ; -WhatIf pour une simulation."
}

# Une seule exécution à la fois (GPO de démarrage + Intune, double clic...).
$mutex = $null
try {
    $mutex = New-Object Threading.Mutex($false, "Global\SpokaDeploy-$Product")
    if (-not $mutex.WaitOne(0)) { Exit-Script $ExitRetry "Une autre installation de $Product est en cours sur ce poste." }
}
catch [Threading.AbandonedMutexException] { }
catch { $mutex = $null }   # simulation sans droits : pas de verrou global

$result = $ExitOk
$plannedVersion = $null
$workDir = $null
$didInstall = $false
$didWriteSettings = $false
try {
    $arch = Get-NativeArchitecture
    $installed = Get-InstalledProduct
    Write-Log "Architecture : $arch ; installé : $(if ($installed) { "$Product $($installed.Version) ($($installed.InstallLocation))" } else { 'non' })"

    # -------------------------------------------------------------------------------------------- installation
    if (-not $ConfigureOnly) {
        $source = $null
        if (-not $SourcePath -and (Find-InstallerInFolder $PSScriptRoot $arch)) { $SourcePath = $PSScriptRoot }
        if ($SourcePath) {
            if (Test-Path -LiteralPath $SourcePath -PathType Container) {
                $found = Find-InstallerInFolder $SourcePath $arch
                if (-not $found) { Exit-Script $ExitError "Aucun installeur $(Get-InstallerName $(if ($Version) { $Version } else { '<version>' }) $arch) dans $SourcePath." }
                $source = [pscustomobject]@{ Path = $found.File; Url = $null; Name = (Split-Path $found.File -Leaf); Version = $found.Version; Sha256 = $null }
            }
            elseif (Test-Path -LiteralPath $SourcePath -PathType Leaf) {
                $item = Get-Item -LiteralPath $SourcePath
                $v = $null
                if ($item.Name -match '-(\d+\.\d+\.\d+)(-arm64)?\.exe$') { $v = [version]$Matches[1] }
                elseif ($item.VersionInfo.FileVersion) { $v = [version]($item.VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*$', '$1') }
                if (($item.Name -match 'arm64') -ne ($arch -eq 'arm64')) { Exit-Script $ExitError "L'installeur $($item.Name) ne correspond pas à l'architecture du poste ($arch)." }
                $source = [pscustomobject]@{ Path = $item.FullName; Url = $null; Name = $item.Name; Version = $v; Sha256 = $null }
            }
            else { Exit-Script $ExitUsage "SourcePath introuvable : $SourcePath" }
            $source.Sha256 = if ($Sha256) { $Sha256.ToLowerInvariant() } else { Get-ExpectedHash $source.Path }
            Write-Log "Source : $($source.Path) (version $($source.Version))"
        }
        else {
            $release = Resolve-GitHubRelease $arch
            $source = [pscustomobject]@{ Path = $null; Url = $release.Url; Name = $release.Name; Version = $release.Version; Sha256 = $release.Sha256 }
            Write-Log "Source : $($release.Url) (version $($release.Version))"
        }
        $plannedVersion = $source.Version
        if (-not $source.Sha256) {
            Exit-Script $ExitError $(if ($source.Path) { "Empreinte SHA-256 inconnue : indiquez -Sha256, ou déposez $($source.Name).sha256 ou SHA256SUMS à côté de l'installeur." } else { "La Release ne publie pas d'empreinte SHA-256 pour $($source.Name) : indiquez -Sha256." })
        }

        $skip = $false
        if ($installed -and $installed.Version -and $source.Version -and -not $Force) {
            if ($installed.Version -eq $source.Version) { Write-Log "$Product $($installed.Version) déjà installé : rien à installer."; $skip = $true }
            elseif ($installed.Version -gt $source.Version) { Write-Log "Version installée ($($installed.Version)) plus récente que $($source.Version) : pas de retour en arrière." 'WARN'; $skip = $true }
        }

        if (-not $skip) {
            # Copie locale, puis vérification et exécution de CETTE copie (le fichier source ne peut plus changer entre-temps).
            $workDir = Join-Path ([IO.Path]::GetTempPath()) "SpokaDeploy-$([guid]::NewGuid().ToString('N'))"
            $local = Join-Path $workDir $source.Name
            if ($PSCmdlet.ShouldProcess($local, 'Copier / télécharger l''installeur')) {
                New-Item -ItemType Directory -Path $workDir | Out-Null
                if ($source.Path) { Copy-Item -LiteralPath $source.Path -Destination $local }
                else { Invoke-Web @{ Uri = $source.Url; OutFile = $local } }
            }
            $hashFile = if (Test-Path -LiteralPath $local) { $local } elseif ($source.Path) { $source.Path } else { $null }
            if ($hashFile) {
                $actual = Get-Sha256 $hashFile
                if ($actual -ne $source.Sha256) { Exit-Script $ExitError "SHA-256 incorrect pour $(Split-Path $hashFile -Leaf) : attendu $($source.Sha256), obtenu $actual. Installeur refusé." }
                Write-Log "SHA-256 vérifié : $actual"
            }
            else { Write-Log "SHA-256 : vérifié après téléchargement (simulation : rien n'est téléchargé)." }

            $tasks = 'addtopath'
            if ($DesktopShortcut) { $tasks += ',desktopicon' }
            $setupLog = if ($script:LogFile) { Join-Path $LogDirectory "Setup-$Product-$stamp.log" } else { $null }
            $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/TASKS=`"$tasks`"")
            if ($setupLog) { $setupArgs += "/LOG=`"$setupLog`"" }
            if ($PSCmdlet.ShouldProcess("$local $($setupArgs -join ' ')", "Installer $Product $($source.Version)")) {
                Write-Log "Installation : $(Split-Path $local -Leaf) $($setupArgs -join ' ')"
                $proc = Start-Process -FilePath $local -ArgumentList $setupArgs -Wait -PassThru -WindowStyle Hidden
                $code = $proc.ExitCode
                Write-Log "Installeur terminé : code $code$(if ($setupLog) { " (journal : $setupLog)" })"
                switch ($code) {
                    0 { }
                    8 { Exit-Script $ExitRetry "Windows attend un redémarrage avant de pouvoir installer $Product (code 8) : relancer après le redémarrage." }
                    default { Exit-Script $ExitError "Échec de l'installation (code Inno Setup $code, voir $setupLog)." }
                }
                $installed = Get-InstalledProduct
                if (-not $installed -or -not (Test-Path -LiteralPath $installed.Cli)) { Exit-Script $ExitError "Installation terminée mais $Product introuvable (clé de désinstallation ou $($P.Cli) absent)." }
                if ($source.Version -and $installed.Version -ne $source.Version) { Exit-Script $ExitError "Version installée $($installed.Version), attendue $($source.Version)." }
                Write-Log "$Product $($installed.Version) installé dans $($installed.InstallLocation)."
                $didInstall = $true
                if (Test-PendingRename $installed.InstallLocation) {
                    Write-Log "Des fichiers de $($installed.InstallLocation) seront remplacés au prochain redémarrage : code 3010." 'WARN'
                    $result = $ExitReboot
                }
            }
            elseif ($WhatIfPreference) { $didInstall = $true }   # simulation : montrer la suite telle qu'elle se déroulerait
        }
    }

    # -------------------------------------------------------------------------------------------- jeton d'enrôlement
    # SpokaPilote 4.0.1 et ultérieurs : le jeton est confié à la ligne de commande installée (« config set ServerToken - »,
    # lu sur l'entrée standard : absent des lignes de commande visibles des autres utilisateurs), qui l'enregistre chiffré
    # dans server-token.dat (SYSTEM et Administrateurs seulement) et ne touche à rien s'il est déjà le même. Il n'est
    # JAMAIS écrit dans settings.json, lisible par tous les utilisateurs du poste.
    $template = $null
    if ($SettingsTemplate) {
        $templatePath = Resolve-InputPath $SettingsTemplate
        try { $template = ConvertTo-SettingsMap (Read-JsonFile $templatePath) (Split-Path $templatePath -Leaf) }
        catch { Exit-Script $ExitUsage "Modèle illisible ($templatePath) : $($_.Exception.Message)" }
        if ($template.Contains('serverToken')) {
            if ($template['serverToken']) {
                $script:Secrets.Add([string]$template['serverToken'])
                Write-Log "Le modèle contient un jeton d'enrôlement : rangez-le là où seuls les comptes ordinateur le lisent (pas dans SYSVOL), ou utilisez -ServerTokenFile." 'WARN'
                if (-not $ServerToken) { $ServerToken = ([string]$template['serverToken']).Trim() }
            }
            $template.Remove('serverToken')
        }
    }
    $settingsFile = Join-Path $DataDirectory 'settings.json'
    $cli = if ($installed) { $installed.Cli } else { Join-Path (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) $Product) $P.Cli }
    $productVersion = if ($didInstall -and $plannedVersion) { $plannedVersion } elseif ($installed) { $installed.Version } else { $null }
    # SpokaPrint, SpokaPilote 4.0.0 et antérieurs : pas de « config set ServerToken - » ; le jeton va dans settings.json.
    $tokenViaCli = $Product -eq 'SpokaPilote' -and $productVersion -and $productVersion -ge [version]'4.0.1'
    $tokenInSettings = $false
    if ($customData) { [Environment]::SetEnvironmentVariable($P.HomeVariable, $DataDirectory, 'Process') }

    if ($ServerToken -and $tokenViaCli) {
        if (-not (Test-Path -LiteralPath $cli) -and -not $WhatIfPreference) { Exit-Script $ExitError "$cli introuvable : impossible d'enregistrer le jeton d'enrôlement." }
        $tokenOutput = @()
        $code = Invoke-ProductCliWithSecret $cli @('config', 'set', 'ServerToken', '-') $ServerToken 'Jeton d''enrôlement (chiffré, server-token.dat)' ([ref]$tokenOutput)
        if ($code -ne 0) { $result = $ExitError; Write-Log "Jeton d'enrôlement non enregistré (code $code) : relancer le script en administrateur ou en SYSTEM." 'ERROR' }
        elseif (@($tokenOutput | Where-Object { "$_" -match 'Jeton enregistr' }).Count -gt 0) { $didWriteSettings = $true }
        elseif ($WhatIfPreference) { $didWriteSettings = $true }   # simulation
    }
    elseif ($ServerToken) {
        $tokenInSettings = $true
        Write-Log "$Product $(if ($productVersion) { $productVersion } else { '(version inconnue)' }) ne sait pas recevoir le jeton hors de settings.json : il y est écrit, lisible par les utilisateurs du poste$(if ($Product -eq 'SpokaPilote') { ' (déployez la 4.0.1 ou ultérieure : le jeton est alors chiffré et réservé aux administrateurs)' })." 'WARN'
    }

    # -------------------------------------------------------------------------------------------- settings.json
    $wantSettings = $SettingsTemplate -or $PSBoundParameters.ContainsKey('ServerUrl') -or $tokenInSettings -or
        $PSBoundParameters.ContainsKey('RepositoryPath') -or $PSBoundParameters.ContainsKey('PolicySource') -or $UseConsoleCache
    # Ancien jeton en clair (kit précédent, version 4.0.0) : retiré de settings.json une fois enregistré à part.
    $legacyToken = $false
    if (-not $wantSettings -and $tokenViaCli -and (Test-Path -LiteralPath $settingsFile)) {
        try { $legacyToken = (ConvertTo-SettingsMap (Read-JsonFile $settingsFile) 'settings.json existant').Contains('serverToken') } catch { }
    }
    if ($wantSettings -or $legacyToken) {
        $map = [ordered]@{}
        if (Test-Path -LiteralPath $settingsFile) {
            $owner = (Get-Acl -LiteralPath $settingsFile).GetOwner([Security.Principal.SecurityIdentifier]).Value
            if ($TrustedOwners -notcontains $owner -and $owner -ne $identity.User.Value) {
                # Fichier créé par un autre compte (un utilisateur standard peut créer des fichiers dans ce dossier sur les
                # versions qui ne le protègent pas) : son contenu n'est pas repris et il est mis de côté.
                Write-Log "settings.json appartient à $owner (ni SYSTEM, ni Administrateurs) : contenu ignoré, fichier renommé." 'WARN'
                if ($PSCmdlet.ShouldProcess($settingsFile, 'Mettre de côté')) { Rename-Item -LiteralPath $settingsFile -NewName "settings.json.ignore-$stamp" }
            }
            else {
                try { $map = ConvertTo-SettingsMap (Read-JsonFile $settingsFile) 'settings.json existant' }
                catch { Exit-Script $ExitError "settings.json existant illisible ($($_.Exception.Message)) : corrigez-le ou supprimez-le, rien n'a été modifié." }
            }
        }
        $before = ConvertTo-Json -InputObject $map -Depth 10

        if ($template) { foreach ($k in @($template.Keys)) { Set-MapValue $map $k $template[$k] } }
        if ($PSBoundParameters.ContainsKey('ServerUrl')) { Set-MapValue $map 'serverUrl' $(if ($ServerUrl) { $ServerUrl.TrimEnd('/') } else { $null }) }
        if ($tokenInSettings) { $map['serverToken'] = $ServerToken }
        elseif ($map.Contains('serverToken')) {
            if ($tokenViaCli -and -not $ServerToken -and $map['serverToken']) {
                # Jeton en clair laissé par un passage précédent : confié à la ligne de commande avant d'être retiré.
                $script:Secrets.Add([string]$map['serverToken'])
                $legacyOutput = @()
                $code = Invoke-ProductCliWithSecret $cli @('config', 'set', 'ServerToken', '-') ([string]$map['serverToken']).Trim() 'Ancien jeton de settings.json (chiffré, server-token.dat)' ([ref]$legacyOutput)
                if ($code -ne 0) { Exit-Script $ExitError "Jeton en clair de settings.json non enregistré à part (code $code) : settings.json inchangé." }
            }
            if ($tokenViaCli) { $map.Remove('serverToken') }
        }
        if ($PSBoundParameters.ContainsKey('RepositoryPath')) { $map['repositoryPaths'] = [string[]]@($RepositoryPath | Where-Object { $_ }) }
        if ($PSBoundParameters.ContainsKey('PolicySource')) { Set-MapValue $map 'policySource' $(if ($PolicySource) { $PolicySource } else { $null }) }
        if ($UseConsoleCache) { $map['useConsoleCache'] = $UseConsoleCache }

        $json = ConvertTo-Json -InputObject $map -Depth 10
        $shown = @($map.Keys | ForEach-Object { if ($_ -eq 'serverToken') { 'serverToken=********' } else { $_ } }) -join ', '
        if ($json -eq $before -and (Test-Path -LiteralPath $settingsFile)) { Write-Log "settings.json déjà conforme : inchangé ($shown)." }
        elseif ($PSCmdlet.ShouldProcess($settingsFile, "Écrire les réglages ($shown)")) {
            New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
            # Écriture en place si le fichier existe (ses droits, posés par l'application, sont conservés) ; sinon
            # création, avec les droits hérités du dossier.
            [IO.File]::WriteAllText($settingsFile, $json, (New-Object Text.UTF8Encoding($false)))
            $didWriteSettings = $true
            Write-Log "settings.json écrit : $settingsFile ($shown)."
        }
        else { $didWriteSettings = $true }   # simulation
    }

    # -------------------------------------------------------------------------------------------- tâches et analyse
    $needCli = $EnableAgent -or $EnableMaintenance -or $InitialScan
    if ($needCli -and -not (Test-Path -LiteralPath $cli) -and -not $WhatIfPreference) { Exit-Script $ExitError "$cli introuvable : $Product n'est pas installé." }

    # Options appliquées à chaque tâche par ce script (une tâche identique déjà en place est conservée).
    $stateFile = Join-Path $DataDirectory 'deploiement.json'
    $state = @{}
    if (Test-Path -LiteralPath $stateFile) {
        try { foreach ($prop in (Read-JsonFile $stateFile).PSObject.Properties) { $state[$prop.Name] = [string]$prop.Value } } catch { }
    }
    function Enable-ProductTask([string]$TaskName, [string[]]$Arguments, [string]$What) {
        $wanted = $Arguments -join ' '
        if (-not $ForceTasks -and $state[$TaskName] -eq $wanted -and (Test-TaskExists $TaskName)) {
            Write-Log "$What : tâche $TaskName déjà en place avec ces options, conservée (-ForceTasks pour la recréer)."
            return $ExitOk
        }
        $code = Invoke-ProductCli $cli $Arguments $What
        if ($code -eq 0 -and -not $WhatIfPreference) {
            $state[$TaskName] = $wanted
            try { [IO.File]::WriteAllText($stateFile, (ConvertTo-Json -InputObject $state), (New-Object Text.UTF8Encoding($false))) }
            catch { Write-Log "État des tâches non enregistré ($stateFile) : $($_.Exception.Message)" 'WARN' }
        }
        return $code
    }

    if ($EnableAgent) {
        $code = Enable-ProductTask '\SpokaPilote\Agent' @('agent', 'enable', '--every', "$AgentEvery") 'Agent (actions à distance)'
        if ($code -ne 0) { $result = $ExitError }
    }
    if ($EnableMaintenance) {
        if ($Product -eq 'SpokaPilote') {
            $a = @('schedule', 'enable')
            switch ($MaintenanceSchedule) {
                'Weekly' { $a += @('--weekly', $MaintenanceDay, '--time', $MaintenanceTime) }
                'Daily' { $a += @('--daily', '--time', $MaintenanceTime) }
                'AtStartup' { $a += '--at-startup' }
            }
            $a += @('--mode', $MaintenanceMode, '--random-delay', "$RandomDelay")
            if ($ReportDirectory) { $a += @('--output-dir', $ReportDirectory) }
            if ($PrinterProfile) { $a += @('--printers', $PrinterProfile) }
        }
        else {
            $a = @('schedule', 'enable', '--profile', $PrinterProfile, '--at-startup', '--daily', $MaintenanceTime, '--random-delay', "$RandomDelay")
            if ($ReportDirectory) { $a += @('--report-dir', $ReportDirectory) }
        }
        $taskName = if ($Product -eq 'SpokaPilote') { '\SpokaPilote\Maintenance' } else { '\SpokaPrint\Deploiement' }
        $code = Enable-ProductTask $taskName $a 'Tâche planifiée'
        if ($code -ne 0) { $result = $ExitError }
    }
    if ($InitialScan -and -not ($didInstall -or $didWriteSettings)) {
        Write-Log "Analyse initiale : ni installation ni changement de réglages pendant ce passage, rien à envoyer."
    }
    elseif ($InitialScan) {
        # Premier rapport : le poste apparaît dans la console. Le résultat de l'analyse (pilotes à traiter) n'est pas
        # un échec du déploiement.
        $code = Invoke-ProductCli $cli @('scan', '--quiet') 'Analyse initiale'
        if ($code -eq 1) { Write-Log "Analyse initiale en erreur : voir %ProgramData%\$Product\Logs (le déploiement reste réussi)." 'WARN' }
    }

    Exit-Script $result $(if ($result -in $ExitOk, $ExitReboot) { "$Product : déploiement terminé." } else { "$Product : installé, mais une étape de configuration a échoué (relancer le script : il est idempotent)." })
}
catch {
    Exit-Script $ExitError "Erreur : $($_.Exception.Message)"
}
finally {
    if ($workDir -and (Test-Path -LiteralPath $workDir)) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($mutex) { try { $mutex.ReleaseMutex() } catch { }; $mutex.Dispose() }
}
