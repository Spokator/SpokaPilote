# Déploiement en entreprise (des centaines à des milliers de postes)

Ce guide décrit comment installer, configurer et suivre SpokaPilote — et SpokaPrint — sur tout un parc avec les outils
habituels de l'IT : **Microsoft Intune**, **Configuration Manager (MECM / SCCM)**, **stratégie de groupe**, outil
**RMM** ou **PowerShell à distance**. Tout passe par un même **kit de déploiement**, dans le dossier
[`deploy/`](../deploy) du dépôt :

| Fichier | Rôle |
|---|---|
| `Install-SpokaPilote.ps1` | Installe ou met à jour en silence (bonne architecture, SHA-256 vérifié), enregistre le jeton d'enrôlement chiffré (jamais dans `settings.json`), écrit `settings.json`, active l'agent et la maintenance. `-Product SpokaPrint` pour SpokaPrint |
| `Uninstall-SpokaPilote.ps1` | Désinstalle en silence, sans laisser de tâche planifiée ; `-RemoveData` pour effacer aussi les données |
| `Detect-SpokaPilote.ps1` · `Detect-SpokaPrint.ps1` | Règle de détection personnalisée (Intune, MECM) : version minimale installée |
| `settings.template.json` | Modèle commenté de `settings.json` (console, dépôt, politique, cache) |
| `Test-DeploymentKit.ps1` | Recette complète du kit sur une machine **jetable** (Windows Sandbox, VM) |

Les scripts sont compatibles **Windows PowerShell 5.1** (présent sur tous les postes) et PowerShell 7, fonctionnent
dans un processus 32 ou 64 bits (Intune lance souvent les commandes en 32 bits), sont **idempotents** (relancés sur un
poste conforme, ils ne changent rien) et n'écrivent **jamais** le jeton d'enrôlement dans un journal ni sur une ligne
de commande. Avec SpokaPilote 4.0.1 et ultérieurs, le jeton n'est pas non plus écrit dans `settings.json` (lisible par
tous les utilisateurs du poste) : le kit le confie à `spokapilote config set ServerToken -` par l'entrée standard, qui
l'enregistre chiffré dans `server-token.dat` (SYSTEM et Administrateurs seulement), et n'y touche pas s'il est déjà le
même — un script de démarrage GPO ou une remédiation Intune relancés à chaque démarrage ne le réécrivent donc jamais en
clair. Chaque script a une aide complète : `Get-Help .\Install-SpokaPilote.ps1 -Full`.

## 1. Ce qui est installé, et où

| Élément | SpokaPilote | SpokaPrint |
|---|---|---|
| Installeur x64 / ARM64 | `SpokaPilote-Setup-<v>.exe` / `SpokaPilote-Installer-<v>-arm64.exe` | `SpokaPrint-Setup-<v>.exe` / `SpokaPrint-Installer-<v>-arm64.exe` |
| Programme | `%ProgramFiles%\SpokaPilote` (`spokapilote.exe` ajouté au PATH) | `%ProgramFiles%\SpokaPrint` (`spokaprint.exe`) |
| Clé de désinstallation (HKLM, vue 64 bits) | `…\Uninstall\{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1` | `…\Uninstall\{37FF2434-ECEF-44C5-983A-AA4DBB7B38FB}_is1` |
| Données, réglages, journaux | `%ProgramData%\SpokaPilote` (`settings.json`, `Logs\`) | `%ProgramData%\SpokaPrint` (`settings.json`, `spokaprint.json`, `Logs\`) |
| Tâches planifiées (SYSTEM) | `\SpokaPilote\Agent`, `\SpokaPilote\Maintenance` | `\SpokaPrint\Deploiement` |

`…\Uninstall\` = `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\`. La valeur `DisplayVersion`
contient la version (`4.0.0`) ; la version de fichier de `spokapilote.exe` est `4.0.0.0`.

Installation silencieuse brute (ce que fait le script) :
`SpokaPilote-Setup-4.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /TASKS="addtopath" /LOG="C:\Windows\Logs\Spoka\setup.log"`.
`/TASKS="addtopath"` ajoute `spokapilote` au PATH sans créer de raccourci sur le Bureau de tous les utilisateurs
(`/TASKS="addtopath,desktopicon"` pour l'avoir). Une mise à jour s'installe par-dessus la version en place : réglages,
historique et tâches planifiées sont conservés. Rien n'est lancé sur le poste en mode silencieux.

### Codes de sortie du kit

| Code | `Install-SpokaPilote.ps1` | `Uninstall-SpokaPilote.ps1` |
|---|---|---|
| `0` | succès (installé, ou déjà conforme) | succès (désinstallé, ou absent) |
| `3010` | succès, des fichiers seront remplacés au redémarrage | succès, des fichiers seront supprimés au redémarrage |
| `1` | échec (téléchargement, empreinte, installeur, configuration) : voir le journal | échec (désinstalleur, tâche non supprimée) |
| `1618` | à réessayer plus tard : une autre exécution est en cours, ou Windows attend un redémarrage avant toute installation (code Inno Setup 8) | — |
| `64` | paramètres invalides (aucune modification) | paramètres invalides |

Ce sont les codes qu'Intune et MECM connaissent déjà (`3010` redémarrage « souple », `1618` nouvelle tentative) :
seul `64` est à déclarer comme échec. Les scripts ne redémarrent **jamais** le poste.

### Journaux

| Journal | Contenu |
|---|---|
| `%windir%\Logs\Spoka\Install-<Produit>-AAAAMMJJ-HHMMSS.log` | chaque étape du script (source, empreinte, version, réglages écrits — jeton masqué —, tâches) |
| `%windir%\Logs\Spoka\Setup-<Produit>-….log` / `Uninstaller-<Produit>-….log` | journal Inno Setup de l'installeur / du désinstalleur |
| `%ProgramData%\SpokaPilote\Logs\spokapilote-AAAAMMJJ.log` | journal de l'application (analyses, envois à la console) |
| `%ProgramData%\SpokaPilote\Logs\maintenance-….log` · `agent-….log` | une exécution de la maintenance planifiée / de l'agent (quand il a eu des tâches) |

`-LogDirectory` change le dossier des journaux du kit (par exemple un dossier collecté par votre outil).

## 2. Préparer le kit

1. **Installeurs officiels** et leur empreinte : téléchargez-les depuis les
   [Releases](https://github.com/Spokator/SpokaPilote/releases), puis comparez avec l'empreinte publiée par GitHub et
   créez le fichier `SHA256SUMS` que le script exige à côté des installeurs :

   ```powershell
   (Invoke-RestMethod https://api.github.com/repos/Spokator/SpokaPilote/releases/tags/v4.0.0).assets | Select-Object name, digest
   Get-ChildItem *.exe | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName).Hash.ToLower(), $_.Name } |
       Set-Content SHA256SUMS -Encoding ASCII
   ```

   Le script refuse tout installeur dont il ne connaît pas l'empreinte : `-Sha256 <empreinte>`, fichier
   `<installeur>.sha256`, `SHA256SUMS` (format `sha256sum`), ou empreinte publiée par GitHub en mode téléchargement.
   Il copie l'installeur dans un dossier temporaire local au nom aléatoire, vérifie **cette copie** puis l'exécute : un
   fichier modifié sur le partage entre-temps n'est jamais lancé.
2. **Réglages communs** : copiez `settings.template.json`, gardez les clés que vous voulez imposer (§ 9).
3. **Jeton d'enrôlement** (page **Enrôlement** de la console, voir [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md#3-enrôlement-des-postes)) :
   dans un fichier `token.txt` d'une ligne, rangé selon le § 12.

Exemple de dossier de paquet (Intune, MECM, partage) :

```text
SpokaPilote-4.0.0\
  Install-SpokaPilote.ps1      Uninstall-SpokaPilote.ps1     Detect-SpokaPilote.ps1
  SpokaPilote-Setup-4.0.0.exe  SpokaPilote-Installer-4.0.0-arm64.exe   SHA256SUMS
  settings.json                token.txt        (paquet Intune ; ailleurs, voir § 12)
```

Sans `-SourcePath`, le script prend l'installeur de l'architecture du poste dans son propre dossier : un seul paquet
sert les PC x64 et ARM64. À défaut, il télécharge la Release GitHub — pratique pour quelques postes, à éviter pour un
parc : l'API GitHub limite à 60 requêtes par heure et par adresse IP publique (avec `-Version` **et** `-Sha256`, le
script télécharge directement sans l'API ; `-Proxy http://proxy:8080` si nécessaire).

Simulation avant toute chose (rien n'est modifié, droits administrateur inutiles) :

```powershell
.\Install-SpokaPilote.ps1 -SettingsTemplate .\settings.json -ServerTokenFile .\token.txt -EnableAgent -EnableMaintenance -WhatIf
```

## 3. Microsoft Intune (application Win32)

### 3.1 Créer le paquet `.intunewin`

Avec l'outil officiel [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) :

```powershell
IntuneWinAppUtil.exe -c C:\Paquets\SpokaPilote-4.0.0 -s Install-SpokaPilote.ps1 -o C:\Paquets\Sortie -q
```

Le fichier `C:\Paquets\Sortie\Install-SpokaPilote.intunewin` contient tout le dossier (≈ 95 Mo avec les deux
installeurs). Intune le chiffre et ne le livre qu'aux postes ciblés.

### 3.2 Programme

| Champ | Valeur |
|---|---|
| Commande d'installation | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-SpokaPilote.ps1 -SettingsTemplate .\settings.json -ServerTokenFile .\token.txt -EnableAgent -EnableMaintenance -InitialScan` |
| Commande de désinstallation | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-SpokaPilote.ps1` |
| Durée d'installation | 30 minutes (installation ≈ 1 min ; l'analyse initiale dure quelques minutes) |
| Comportement d'installation | **Système** |
| Comportement de redémarrage | Déterminer le comportement en fonction des codes de retour |
| Codes de retour | ceux par défaut (`0`, `1707`, `3010`, `1641`, `1618`) + **`64` = Échec** |

`powershell.exe` suffit : le script fonctionne en 32 comme en 64 bits (registre lu en vue 64 bits, architecture lue
dans le système, `Program Files` réel). Pas besoin de `Sysnative`.

### 3.3 Configuration requise

- **Architecture du système d'exploitation** : x64 **et** ARM64 si le paquet contient les deux installeurs ; sinon un
  paquet (et une application Intune) par architecture.
- **Système d'exploitation minimal** : Windows 10 1809 (l'installeur refuse les versions antérieures).

### 3.4 Règle de détection

Au choix (une seule suffit) :

| Type | Réglages |
|---|---|
| **Registre** (recommandé) | Clé `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1`, valeur `DisplayVersion`, méthode **Comparaison de versions**, **Supérieur ou égal à** `4.0.0`, *Associé à une application 32 bits sur des clients 64 bits* : **Non** |
| **Fichier** | Chemin `%ProgramFiles%\SpokaPilote`, fichier `spokapilote.exe`, méthode **Version**, **Supérieur ou égal à** `4.0.0.0`, *Associé à une application 32 bits* : **Non** |
| **Script personnalisé** | `Detect-SpokaPilote.ps1` (modifiez `$MinimumVersion` ; `$RequireAgentTask = $true` pour exiger aussi l'agent) — *Exécuter en 32 bits* : Non, *Appliquer la vérification de signature* : Non (sauf script signé) |

À chaque nouvelle version, montez la version minimale de la règle : c'est elle qui déclenche l'installation de la mise
à jour sur les postes.

### 3.5 Mises à jour : remplacement et dépendances

- **Remplacement (supersedence)** : créez une application par version (« SpokaPilote 4.0.1 ») avec une relation de
  remplacement vers la précédente, **Désinstaller l'application précédente : Non** — l'installeur met à jour en place
  et conserve réglages et tâches. Les postes de l'ancienne version reçoivent la nouvelle dès qu'elle leur est affectée.
- **Dépendances** : inutiles dans le cas général (l'application installe et configure). SpokaPrint ne dépend pas de
  SpokaPilote. Si vous séparez l'installation et la configuration (deux applications, par exemple pour changer de
  console sans redistribuer 95 Mo), la seconde (`-ConfigureOnly`) dépend de la première.
- **Changer de console ou de jeton** sans réinstaller : une application (ou un script de plateforme Intune, contexte
  système) `Install-SpokaPilote.ps1 -ConfigureOnly -ServerUrl … -ServerTokenFile .\token.txt`.

### 3.6 Anneaux d'affectation

Affectez chaque version comme **Obligatoire** à des groupes d'appareils successifs, par exemple :

| Anneau | Groupe Entra ID | Part du parc | Délai après la publication |
|---|---|---|---|
| 0 — IT | `SpokaPilote - Anneau 0 - IT` | postes de l'équipe | J |
| 1 — Pilote | `SpokaPilote - Anneau 1 - Pilote` | 2 à 5 %, tous modèles et sites | J+2 |
| 2 — Large | `SpokaPilote - Anneau 2 - Large` | ≈ 25 % | J+7 |
| 3 — Général | `SpokaPilote - Anneau 3 - Général` (ou tous les appareils, en excluant les précédents) | le reste | J+14 |

Avant d'élargir : rapport d'installation de l'application dans Intune (échecs, codes), et page **Postes** de la console
(les postes de l'anneau remontent-ils leur rapport ?). Ces anneaux portent sur la **version de SpokaPilote** ; la
diffusion des **pilotes** par anneaux se règle dans la console ([POLITIQUE.md](POLITIQUE.md#anneaux-de-déploiement-et-suspension-automatique)).
Les deux se combinent bien en réutilisant les mêmes postes pilotes.

Le bouton de mise à jour de l'interface et l'action à distance `self-update` de la console installent la dernière
Release : sur un parc géré par Intune, laissez Intune porter les versions (règle de détection) pour garder le contrôle
des anneaux.

### 3.7 SpokaPrint

Même méthode, avec `-Product SpokaPrint` :

- installation : `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-SpokaPilote.ps1 -Product SpokaPrint -ServerTokenFile .\token.txt -ServerUrl https://spokapilote.mon-entreprise.fr -EnableMaintenance -PrinterProfile console`
  (`-PrinterProfile` : fichier, partage, URL HTTPS ou `console` ; la tâche `\SpokaPrint\Deploiement` s'exécute au
  démarrage et chaque jour à `-MaintenanceTime`, délai aléatoire `-RandomDelay` plafonné à 240 min) ;
- désinstallation : `… -File .\Uninstall-SpokaPilote.ps1 -Product SpokaPrint` ;
- détection : clé `…\Uninstall\{37FF2434-ECEF-44C5-983A-AA4DBB7B38FB}_is1` (`DisplayVersion` ≥ `1.0.0`), fichier
  `%ProgramFiles%\SpokaPrint\spokaprint.exe` (≥ `1.0.0.0`) ou `Detect-SpokaPrint.ps1`.

Les réglages propres à SpokaPrint (`SnmpCommunity`, `ScanParallelism`…, fichier `spokaprint.json`) se règlent avec
`spokaprint config set <clé> <valeur>` (voir la documentation de SpokaPrint) ; `settings.json` porte les réglages du
moteur commun (`serverUrl`, `serverToken`, `printerCatalogUrl`, `repositoryPaths`).

## 4. Configuration Manager (MECM / SCCM)

1. **Bibliothèque de logiciels › Applications › Créer une application** › *Spécifier manuellement les informations*.
2. **Type de déploiement** : *Programme d'installation de script*.
   - Emplacement du contenu : le dossier du kit sur un partage (`\\srv-sccm\sources$\SpokaPilote\4.0.0`).
   - Programme d'installation : `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-SpokaPilote.ps1 -SettingsTemplate .\settings.json -ServerTokenFile \\srv-fichiers\spoka$\token.txt -EnableAgent -EnableMaintenance -InitialScan`
   - Programme de désinstallation : `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-SpokaPilote.ps1`
3. **Méthode de détection** : *Paramètre* › type **Registre**, ruche `HKEY_LOCAL_MACHINE`, clé
   `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1`, valeur
   `DisplayVersion`, type de données **Version**, *doit satisfaire la règle* **Supérieur ou égal à** `4.0.0` ; décochez
   *Ce paramètre de Registre est associé à une application 32 bits sur des systèmes 64 bits*. Ou bien : *Utiliser un
   script personnalisé*, PowerShell, `Detect-SpokaPilote.ps1` (même convention qu'Intune : détecté si le script écrit
   quelque chose et renvoie 0).
4. **Expérience utilisateur** : *Installer pour le système*, *Que l'utilisateur soit connecté ou non*, visibilité
   *Masqué*, durée d'exécution maximale 30 minutes.
5. **Codes de retour** : ceux par défaut (`0`, `1707`, `3010`, `1641`, `1618` nouvelle tentative rapide) + `64`
   *Échec*.
6. **Configuration requise** : un seul type de déploiement suffit pour x64 et ARM64 (le script choisit l'installeur).
   Pour séparer, une condition globale de type Registre sur
   `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment\PROCESSOR_ARCHITECTURE` (`AMD64` ou `ARM64`).
7. **Mises à jour** : nouvelle application par version, onglet **Remplacement** vers la précédente, *Désinstaller* non
   coché. **Déploiement** obligatoire par regroupements successifs (*déploiement échelonné* de MECM, ou un
   regroupement par anneau comme au § 3.6).

Le contenu d'un point de distribution peut être lu par d'autres que les postes ciblés : laissez le jeton **hors** du
contenu et pointez `-ServerTokenFile` vers un partage restreint (§ 12).

## 5. Stratégie de groupe (GPO)

### 5.1 Script de démarrage de l'ordinateur (recommandé)

Préparez un partage `\\srv-fichiers\spoka$` : `kit\` (scripts, installeurs, `SHA256SUMS`, `settings.json`) en
**lecture** pour *Ordinateurs du domaine* (modification réservée à l'IT), et `secret\token.txt` lisible **uniquement**
par *Ordinateurs du domaine* et les administrateurs (droits de partage et NTFS ; retirez *Utilisateurs authentifiés*).
Au démarrage, le script s'exécute en SYSTEM et accède au réseau avec le compte ordinateur.

**Configuration ordinateur › Stratégies › Paramètres Windows › Scripts (démarrage/arrêt) › Démarrage › Scripts**
(onglet *Scripts*, pas *Scripts PowerShell*, pour maîtriser la stratégie d'exécution) :

- Nom du script : `%windir%\System32\WindowsPowerShell\v1.0\powershell.exe`
- Paramètres :
  `-NoProfile -ExecutionPolicy Bypass -File \\srv-fichiers\spoka$\kit\Install-SpokaPilote.ps1 -SourcePath \\srv-fichiers\spoka$\kit -SettingsTemplate \\srv-fichiers\spoka$\kit\settings.json -ServerTokenFile \\srv-fichiers\spoka$\secret\token.txt -EnableAgent -EnableMaintenance -InitialScan`

Le script s'exécute à **chaque** démarrage : sur un poste à jour il ne fait que lire la clé de désinstallation et
`settings.json` (une à deux secondes), ne réécrit rien, conserve les tâches existantes (une tâche recréée perdrait le
rattrapage d'une exécution manquée ; `-ForceTasks` pour forcer) et ne relance l'analyse qu'après une installation, une
mise à jour ou un changement de réglages. Pour une nouvelle version : déposez l'installeur et mettez à jour
`SHA256SUMS` ; le script prend la plus récente (ou celle de `-Version`).

Activez aussi *Configuration ordinateur › Modèles d'administration › Système › Ouverture de session › Toujours attendre
le réseau lors du démarrage de l'ordinateur et de l'ouverture de session* : sans réseau, le partage est injoignable et
le script s'arrête en erreur (il réessaiera au démarrage suivant). Les scripts de démarrage sont limités à 10 minutes
par défaut (*Délai d'attente maximal pour les scripts de stratégie de groupe*).

> **Ne mettez jamais le jeton dans les paramètres du script ni dans un fichier de SYSVOL / NETLOGON** : ces emplacements
> sont lisibles par **tous les utilisateurs du domaine**.

### 5.2 `settings.json` par les préférences (GPP Fichiers)

Possible pour les réglages **sans secret** : *Configuration ordinateur › Préférences › Paramètres Windows › Fichiers*,
action **Remplacer**, source `\\srv-fichiers\spoka$\kit\settings.json`, destination
`%CommonAppDataDir%\SpokaPilote\settings.json`. Limites : le fichier est **réécrit en entier** à chaque
actualisation de la stratégie (toute autre clé est perdue), et il faut des clés en camelCase exact (§ 9). N'y mettez
**jamais** `serverToken` : le fichier recopié serait lisible par tous les utilisateurs jusqu'au démarrage élevé suivant
(qui l'en retire), puis recopié en clair à l'actualisation suivante. Le script de démarrage, qui **fusionne** les réglages, est préférable ; ne
combinez pas les deux.

## 6. RMM et PowerShell à distance

**RMM** (NinjaOne, Datto RMM, N-able, ConnectWise Automate, Tanium…) : les scripts s'exécutent en SYSTEM. Joignez le
kit au script (ou un partage lisible par les comptes ordinateur) et passez le jeton par une **variable secrète** de
l'outil, exposée en variable d'environnement :

```powershell
& "$env:TEMP\kit\Install-SpokaPilote.ps1" -SourcePath "$env:TEMP\kit" -ServerUrl https://spokapilote.mon-entreprise.fr `
    -ServerTokenVariable SPOKA_TOKEN -EnableAgent -EnableMaintenance -InitialScan
exit $LASTEXITCODE
```

**PowerShell à distance** (quelques dizaines à quelques centaines de postes) : une session distante n'a pas accès aux
partages réseau avec vos identifiants (« double saut » Kerberos). Copiez le kit sur chaque poste, puis lancez-le :

```powershell
$sessions = New-PSSession -ComputerName (Get-Content .\postes.txt)
foreach ($s in $sessions) { Copy-Item -ToSession $s -Path .\kit -Destination C:\Windows\Temp\SpokaKit -Recurse -Force }
Invoke-Command -Session $sessions -ThrottleLimit 32 -ScriptBlock {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Windows\Temp\SpokaKit\Install-SpokaPilote.ps1 `
        -ServerUrl https://spokapilote.mon-entreprise.fr -ServerTokenFile C:\Windows\Temp\SpokaKit\token.txt -EnableAgent -InitialScan | Out-Null
    [pscustomobject]@{ Code = $LASTEXITCODE }
    Remove-Item C:\Windows\Temp\SpokaKit -Recurse -Force
} | Sort-Object Code | Format-Table PSComputerName, Code
$sessions | Remove-PSSession
```

Au-delà, préférez Intune, MECM, la GPO ou le RMM : ils gèrent les postes éteints, les nouvelles tentatives et le suivi.

## 7. winget

SpokaPilote n'est **pas encore publié** dans le dépôt communautaire winget (manifestes prêts dans `packaging/winget/`,
soumission manuelle : [DISTRIBUTION.md](DISTRIBUTION.md)). Une fois publié :

```powershell
winget install --id Spoka.SpokaPilote --exact --silent --scope machine --accept-package-agreements --accept-source-agreements
```

En SYSTEM (Intune, RMM), `winget` n'est pas dans le PATH : il se trouve sous
`%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe`. winget installe la
dernière version publiée et ne configure rien : faites suivre `Install-SpokaPilote.ps1 -ConfigureOnly …`. Pour un parc,
le kit (version figée, empreinte vérifiée, anneaux) reste préférable.

## 8. MSIX

Le paquet MSIX sert à la publication dans le Microsoft Store ([PUBLICATION-STORE.md](PUBLICATION-STORE.md)). Il n'est
pas conseillé pour un parc géré : l'agent et la maintenance planifiée sont des tâches SYSTEM qui lancent l'exécutable
par son chemin, or celui d'une application MSIX change à chaque mise à jour (dossier versionné sous `WindowsApps`).
Déployez l'installeur classique avec le kit.

## 9. Configuration commune

### `settings.json`

`%ProgramData%\SpokaPilote\settings.json` est commun à tout le poste. **Les clés sont en camelCase et sensibles à la
casse** : `"ServerUrl"` est ignoré sans avertissement, seul `"serverUrl"` est lu. Le kit corrige la casse des clés
connues (avec un avertissement dans le journal).

| Clé | Défaut | Rôle |
|---|---|---|
| `serverUrl` | — | console centrale (HTTPS obligatoire, sauf `localhost` ; adresse finale, sans redirection) |
| `serverToken` | — | jeton d'**enrôlement** (jamais le jeton administrateur). **4.0.1 et ultérieurs : ne le mettez pas dans `settings.json`** ; il est enregistré chiffré dans `server-token.dat` (kit, `config set ServerToken -`). Un jeton trouvé dans `settings.json` y est retiré au démarrage élevé suivant (mode portable : reste dans `settings.json` de la clé) |
| `repositoryPaths` | `[]` | dépôts de pilotes indexés en priorité (partages lisibles par les comptes ordinateur) |
| `policySource` | — | politique de pilotes : chemin, UNC ou HTTPS ; sinon celle de la console ([POLITIQUE.md](POLITIQUE.md#déployer-sur-le-parc)) |
| `useConsoleCache` | `auto` | cache de pilotes de la console : `auto`, `on` (avertit à chaque repli), `off` |
| `consoleCacheCatalogs` | `true` | catalogues aussi relayés par la console |
| `useWindowsUpdate` · `windowsUpdateBypassWsus` · `useMicrosoftCatalog` · `useOemCatalogs` · `useGpuVendorSources` | `true` | sources de pilotes |
| `nvidiaBranch` | `studio` | `studio` ou `gameready` |
| `includeBiosAndFirmware` | `false` | proposer aussi BIOS et micrologiciels |
| `createRestorePoint` · `backupDriversBeforeInstall` | `true` | filets de sécurité avant installation |
| `extraTrustedHosts` · `extraTrustedPublishers` | `[]` | domaines / éditeurs supplémentaires autorisés ; un éditeur s'écrit avec sa forme juridique, tel qu'il apparaît dans le certificat (`O=`) : « Fabrikam, Inc. », pas « Fabrikam » (4.0.1 : correspondance exacte) |
| `printerCatalogUrl` | — | catalogue d'imprimantes de l'organisation (HTTPS) |
| `catalogConcurrency` | `4` | requêtes simultanées vers les catalogues |

Avec le kit : `-SettingsTemplate` (fichier JSON fusionné), puis `-ServerUrl`, `-RepositoryPath` (remplace la liste),
`-PolicySource`, `-UseConsoleCache`. Les autres clés du poste sont conservées ; une valeur `null` dans le modèle retire
la clé. Un `settings.json` illisible arrête le script sans rien modifier.

Le jeton (`-ServerTokenFile`, `-ServerTokenVariable`, `-ServerToken`, ou `serverToken` d'un modèle, déconseillé) suit
un autre chemin : SpokaPilote 4.0.1 et ultérieurs le reçoivent par `spokapilote config set ServerToken -` (entrée
standard : absent de la ligne de commande du processus, visible des autres utilisateurs du poste et des journaux
d'audit), l'enregistrent chiffré dans `server-token.dat`, et ne modifient rien si le jeton enregistré est déjà le même.
Un ancien jeton en clair laissé dans `settings.json` par un kit précédent est enregistré à part puis retiré du fichier.
SpokaPrint et SpokaPilote 4.0.0 ne connaissent pas cette commande : le kit écrit alors le jeton dans `settings.json`,
avec un avertissement dans son journal (déployez la 4.0.1 avant, ou avec, le jeton).

### Sans le kit : la ligne de commande

En SYSTEM ou console administrateur (`spokapilote` est dans le PATH) :

```powershell
spokapilote config set ServerUrl https://spokapilote.mon-entreprise.fr
spokapilote config set PolicySource \\srv-fichiers\deploiement$\SpokaPilote\driver-policy.json
spokapilote config set UseConsoleCache auto
spokapilote config add-repo \\srv-fichiers\pilotes$
spokapilote server status          # adresse, certificat HTTPS, jeton
```

Le jeton se donne par l'**entrée standard** (4.0.1 et ultérieurs) : `config set ServerToken <jeton>` laisserait le
jeton sur la ligne de commande, conservée par les journaux d'audit (événement 4688, EDR, outil de déploiement) et
visible des autres utilisateurs du poste pendant l'exécution.

```powershell
Get-Content \\srv-fichiers\spoka$\secret\token.txt -TotalCount 1 | spokapilote config set ServerToken -
spokapilote config set ServerToken -        # en console : saisie masquée
```

Le jeton est enregistré chiffré dans `server-token.dat` (jamais dans `settings.json`) ; un jeton identique ne change
rien. Ne journalisez pas la sortie de `spokapilote config` d'un poste en 4.0.0 : elle affiche le jeton en clair.

## 10. Agent et maintenance à grande échelle

- **Agent** (`-EnableAgent`, `spokapilote agent enable --every 15`) : tâche `\SpokaPilote\Agent`, toutes les 5 à
  1440 minutes, plus un délai aléatoire d'une minute. Une interrogation sans tâche ne coûte qu'une requête : 10 000
  postes toutes les 15 minutes font environ 11 requêtes par seconde, que la console absorbe sans peine
  ([CONSOLE-CENTRALE.md, § 11](CONSOLE-CENTRALE.md#11-grands-parcs)). L'agent n'exécute rien tant que le poste n'a pas
  envoyé un premier rapport : d'où `-InitialScan`.
- **Maintenance** (`-EnableMaintenance`) : tâche `\SpokaPilote\Maintenance`, par défaut chaque lundi à 12:00, mode
  `missing`, avec `-RandomDelay 120` (2 heures, maximum 720) pour étaler les téléchargements ; `-MaintenanceSchedule
  Daily|AtStartup`, `-MaintenanceMode updates|scan`, `-ReportDirectory \\srv\rapports` (écriture pour *Ordinateurs du
  domaine*). Détail des options : [IMPRIMANTES-ET-PLANIFICATION.md](IMPRIMANTES-ET-PLANIFICATION.md#2-maintenance-planifiée).
  Avec une console, la maintenance n'est pas indispensable : les **actions planifiées** de la console (§ 5 de
  [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md#actions-planifiées-31)) passent par l'agent et se pilotent par groupe.
- **Cache de pilotes de la console** (`useConsoleCache`) : chaque fichier officiel n'est téléchargé qu'une fois pour
  tout le site au lieu d'une fois par poste.
- **Analyse initiale** (`-InitialScan`) : des milliers d'analyses simultanées sont étalées naturellement par Intune et
  MECM ; si un afflux sature la console, elle répond `503` avec un délai et le poste garde son rapport local (voir
  « Afflux de postes » au § 11 de [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md#afflux-de-postes-coupure-de-courant-campagne-danalyses)).

## 11. Vérifier un déploiement

1. **Outil de déploiement** : codes de sortie par poste (§ 1). `64` = commande à corriger ; `1` = lire
   `%windir%\Logs\Spoka\Install-*.log` (étape en échec, puis le journal `Setup-*.log` de l'installeur).
2. **Sur un poste** (console administrateur) :

   ```powershell
   spokapilote --version
   spokapilote server status      # console joignable, certificat HTTPS accepté, jeton valide
   spokapilote agent status       # tâche, dernière exécution, dernier contact avec la console
   spokapilote schedule status    # maintenance : prochaine / dernière exécution et son résultat
   Get-Content (Get-ChildItem C:\Windows\Logs\Spoka\Install-SpokaPilote-*.log | Sort-Object Name | Select-Object -Last 1).FullName
   ```
3. **Console** : page **Postes** (nombre de postes, date du dernier rapport) ; la fiche d'un poste indique sa version
   de SpokaPilote et le dernier contact. Un poste installé qui n'apparaît pas : `server status` sur ce poste
   (jeton, certificat, proxy), puis `%ProgramData%\SpokaPilote\Logs\spokapilote-*.log` (« Rapport non envoyé … »).
4. **À l'échelle du parc** : comparez le nombre de postes de l'anneau dans l'outil de déploiement et dans la console ;
   `Detect-SpokaPilote.ps1` peut aussi servir de script de conformité (Intune *Remédiations*, élément de configuration
   MECM).

## 12. Sécurité

- **Jeton d'enrôlement** : il permet d'envoyer des rapports et de récupérer les tâches des postes, pas d'administrer la
  console ; il reste à protéger. Sources sûres : le paquet Intune (chiffré, livré aux seuls postes ciblés), un partage
  lisible uniquement par *Ordinateurs du domaine* (`-ServerTokenFile \\srv\spoka$\secret\token.txt`), une variable
  secrète du RMM (`-ServerTokenVariable`). À proscrire : SYSVOL / NETLOGON, paramètres d'une GPO, ligne de commande
  d'un outil qui la journalise, contenu d'un point de distribution MECM. Le kit ne l'écrit dans aucun journal (il le
  masque même si une commande le renvoyait). En cas de fuite : changez-le dans la console (§ 2 de
  [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md#2-jetons-comptes-et-rôles)) puis redéployez avec `-ConfigureOnly`.
- **Stockage du jeton sur le poste (4.0.1 et ultérieurs)** : `%ProgramData%\SpokaPilote\server-token.dat`, lisible des
  seuls SYSTEM et Administrateurs, chiffré par DPAPI « machine ». C'est la **DACL** qui le protège des utilisateurs du
  poste : Microsoft documente qu'avec `CRYPTPROTECT_LOCAL_MACHINE`, *tout utilisateur de l'ordinateur* peut déchiffrer
  la donnée ([CryptProtectData, Remarques](https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata#remarks)).
  DPAPI rend inexploitable une copie du fichier **seul** (sauvegarde de fichiers, copie vers un autre PC) ; une **image
  disque complète** ou une sauvegarde système contient les ruches `SYSTEM` et `SECURITY` qui permettent de le déchiffrer
  hors ligne : protégez ces sauvegardes comme le poste, et changez le jeton en cas de doute.
- **Images de référence (sysprep, clonage)** : **n'intégrez jamais le jeton dans une image de référence.** Que
  `sysprep /generalize` conserve ou non la clé DPAPI machine n'est pas documenté par Microsoft (la documentation de
  [Sysprep](https://learn.microsoft.com/windows-hardware/manufacture/desktop/sysprep-command-line-options#generalize)
  indique seulement que les informations propres au système sont retirées) : un poste cloné peut ne pas pouvoir le
  lire. Déployez le jeton **après** sysprep, sur chaque poste (kit en tâche de fin de séquence MECM / MDT, application
  Intune, script de démarrage). Si un poste trouve un `server-token.dat` indéchiffrable, il l'écrit dans son journal
  (« Jeton de la console indéchiffrable sur ce poste »), supprime le fichier et continue sans console jusqu'au
  redéploiement du jeton.
- **Droits sur `%ProgramData%\SpokaPilote`** : **SpokaPilote 4.0.1 et ultérieures protègent ce dossier : seuls les
  administrateurs peuvent le modifier, et le jeton d'enrôlement n'est pas lisible par les utilisateurs standard.**
  En **4.0.0**, le dossier hérite des droits de `%ProgramData%` : tout utilisateur du poste peut **lire**
  `settings.json` (donc le jeton) et **créer** des fichiers dans le dossier. Sur un parc en 4.0.0, déployez la 4.0.1
  avant (ou avec) le jeton ; le kit l'indique dans son journal.
- **`SPOKAPILOTE_HOME`** (essais, `-DataDirectory`) sur un volume **FAT32 / exFAT** ou un **partage réseau** : ces
  emplacements n'ont pas de propriétaires ni d'ACL fiables. SpokaPilote les traite comme une clé portable (aucun droit
  modifié, rien de mis en quarantaine, jeton dans `settings.json`, contenu cru par les exécutions élevées) et l'écrit
  dans son journal à chaque démarrage élevé : ne les utilisez pas sur un poste de production. Un dossier existant
  n'est protégé (repris, mis en quarantaine) que s'il est vide, déjà protégé, ou ne contient que des éléments de
  SpokaPilote dont `settings.json`.
- **Le kit** n'écrit `settings.json` qu'en administrateur ou en SYSTEM. Il modifie un fichier existant **en place**
  (les droits posés par l'application sont conservés) ou le crée avec les droits hérités du dossier. Un
  `settings.json` dont le propriétaire n'est ni SYSTEM, ni les Administrateurs, ni le compte qui exécute le script
  (fichier créé par un utilisateur standard) n'est **pas** repris : il est renommé `settings.json.ignore-<date>` et
  remplacé.
- **Installeurs** : SHA-256 vérifié sur une copie locale avant exécution ; seuls les installeurs officiels nommés
  `SpokaPilote-Setup-<v>.exe` / `-Installer-<v>-arm64.exe` sont pris dans un dossier. Les installeurs ne sont signés
  que si un certificat est configuré ([SIGNATURE.md](SIGNATURE.md)) : l'empreinte est alors le contrôle d'intégrité.
- **Partages** : dépôt de pilotes, kit et politique en lecture seule pour les postes, modification réservée à l'IT ;
  dossier de rapports en écriture seule si possible.
- **Stratégie d'exécution** : les commandes utilisent `-ExecutionPolicy Bypass` pour ce seul processus. Si votre
  organisation impose des scripts signés (*AllSigned*), signez les scripts du kit avec votre certificat de code
  (`Set-AuthenticodeSignature`) et retirez `-ExecutionPolicy Bypass`.

## 13. Désinstallation

```powershell
.\Uninstall-SpokaPilote.ps1                       # programme et tâches ; réglages et journaux conservés
.\Uninstall-SpokaPilote.ps1 -RemoveData            # + %ProgramData%\SpokaPilote (sauvegardes de pilotes conservées)
.\Uninstall-SpokaPilote.ps1 -RemoveData -RemoveBackups
.\Uninstall-SpokaPilote.ps1 -Product SpokaPrint
```

Le script supprime les tâches `\SpokaPilote\Maintenance`, `\SpokaPilote\Agent` et `\SpokaPilote\Analyse` (proposée au
§ 3 de [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md#3-enrôlement-des-postes)) avant et après la désinstallation, puis le
dossier `\SpokaPilote` du Planificateur s'il est vide. C'est indispensable pour les postes en **4.0.0** : leur
désinstalleur laisse ces tâches, qui lanceraient un exécutable supprimé. Les désinstalleurs suivants les retirent
eux-mêmes. Le désinstalleur seul (`"%ProgramFiles%\SpokaPilote\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
/NORESTART`) conserve les réglages et les journaux en mode silencieux.

À partir de la 4.0.1, le désinstalleur supprime **toujours** le jeton (`server-token.dat`, un secret) : une mise à jour
ne désinstalle rien et le conserve ; une réinstallation le redéploie (kit, Intune). Quand l'utilisateur choisit de
supprimer aussi les données (question posée par une désinstallation interactive), ou avec `-RemoveData`, partent
également `Quarantaine` (qui peut contenir un ancien `settings.json` avec un jeton en clair), `Work`, les caches de
politique et de décisions de diffusion, l'état du kit (`deploiement.json`) et le témoin de protection. Le dossier
`Mount` n'est supprimé que s'il est vide : une image Windows encore montée se démonte d'abord (`spokapilote image
cleanup` ou `dism /Cleanup-Mountpoints`).

## 14. Recette du kit sur une machine jetable

`Test-DeploymentKit.ps1` enchaîne installation, configuration (console fictive), idempotence, mise à jour,
désinstallation, et vérifie à chaque étape la version, les tâches, la détection, l'absence du jeton dans les journaux,
puis qu'**aucune tâche** ne reste. Il refuse de tourner sur un poste où SpokaPilote est déjà installé. Dans
**Windows Sandbox** (fonctionnalité *Windows Sandbox* de Windows 11 Pro / Entreprise), un fichier `recette.wsb` :

```xml
<Configuration>
  <MappedFolders>
    <MappedFolder><HostFolder>C:\Sources\SpokaPilote</HostFolder><SandboxFolder>C:\Kit</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>C:\Sources\Installeurs</HostFolder><SandboxFolder>C:\Installeurs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  </MappedFolders>
</Configuration>
```

puis, dans la Sandbox, PowerShell **administrateur** :

```powershell
Set-ExecutionPolicy -Scope Process Bypass
C:\Kit\deploy\Test-DeploymentKit.ps1 -InstallerFolder C:\Installeurs -FromVersion 4.0.0 -ToVersion 4.0.1
C:\Kit\deploy\Test-DeploymentKit.ps1 -InstallerFolder C:\Installeurs -FromVersion 4.0.1 -RawUninstall   # désinstalleur seul
```

(`C:\Installeurs` contient les installeurs et `SHA256SUMS`.) Le second appel vérifie que le désinstalleur lui-même ne
laisse aucune tâche (versions postérieures à 4.0.0).

## 15. Limites connues

- **Validé sur un poste réel, sans rien modifier** : règles de détection (versions 4.0.0 et SpokaPrint 1.0.0 installées,
  PowerShell 32 et 64 bits), choix de l'installeur et de l'architecture, vérification SHA-256 (bonne, mauvaise,
  absente, `SHA256SUMS`, `.sha256`, empreintes publiées par GitHub), lecture de la Release GitHub, fusion de
  `settings.json` dans un dossier d'essai (casse des clés, commentaires, idempotence, relecture par l'application),
  absence du jeton dans les journaux, codes `64`, et tous les parcours en simulation (`-WhatIf`) dont la
  désinstallation ; compilation de l'installeur modifié.
- **Jeton par la ligne de commande (4.0.1)** : exécuté en administrateur avec une copie du kit pointant vers la ligne
  de commande compilée et un dossier de données d'essai : premier enregistrement, relance identique (ni le jeton ni
  `settings.json` réécrits), ancien jeton en clair retiré de `settings.json`, rotation du jeton, absence du jeton dans
  les journaux et dans `settings.json`.
- **Pas encore exécuté pour de vrai** (droits administrateur et machine jetable nécessaires) : installation,
  mise à jour et désinstallation silencieuses par le kit, création des tâches, suppression des tâches par le nouveau
  désinstalleur. `Test-DeploymentKit.ps1` (§ 14) fait cette recette ; menez-la avant le premier anneau.
- Le kit ne règle pas `spokaprint.json` (réglages propres à SpokaPrint) : `spokaprint config set`.
- PowerShell à distance : pas d'accès aux partages depuis la session (double saut) ; le kit doit être copié sur le poste.
