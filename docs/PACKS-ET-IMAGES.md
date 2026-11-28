# Packs de pilotes constructeurs et injection dans les images Windows

Deux outils pour les déploiements de parc :

- **`spokapilote driverpack`** : télécharge le *pack complet de pilotes* publié par Dell, HP, Lenovo ou Microsoft
  (Surface) pour un modèle (les « driver packs » destinés à SCCM / MDT) et le range dans votre dépôt. Plus besoin de préparer un
  poste de référence : tous les PC de ce modèle s'installent ensuite hors ligne depuis le dépôt.
- **`spokapilote image`** : injecte les pilotes d'un dépôt dans une image Windows (`install.wim`, `boot.wim`,
  WinPE) ou dans un Windows hors ligne, avec DISM, l'outil intégré à Windows.

Les deux appliquent la même chaîne de contrôle que le reste de SpokaPilote : sources officielles uniquement,
empreinte vérifiée, signatures vérifiées, rien d'exécuté qui ne soit signé par le constructeur.

---

## 1. Packs de pilotes officiels

### D'où viennent les packs

| Constructeur | Catalogue officiel (mis en cache 24 h) | Identifiant du modèle | Empreinte |
|---|---|---|---|
| Dell | `downloads.dell.com/catalog/DriverPackCatalog.cab` (signé Dell) | SKU système, ex. `0A36` (Latitude 7420) | SHA-256 + MD5 |
| HP | `hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab` (signé HP) | plateforme (carte mère), ex. `8549` (EliteBook 840 G6) | SHA-256 |
| Lenovo | `download.lenovo.com/cdrt/td/catalogv2.xml` | type-machine (4 premiers caractères du modèle), ex. `20WM` | SHA-256 |
| Microsoft Surface | table embarquée → page du Centre de téléchargement `www.microsoft.com/en-us/download/details.aspx?id=…` (voir § Surface) | SKU système SMBIOS, ex. `Surface_Pro_9_for_Business_2038` | aucune publiée : signature Microsoft + taille |

Les packs eux-mêmes sont téléchargés depuis `downloads.dell.com`, `ftp.hp.com`, `download.lenovo.com` et
`download.microsoft.com` (HTTPS obligatoire, redirections comprises) : aucun hôte supplémentaire n'est nécessaire
(`download.microsoft.com` figurait déjà dans la liste des sources officielles ; la page `www.microsoft.com` n'est que
lue, jamais source d'un fichier téléchargé).

### Trouver le pack d'un modèle

```powershell
spokapilote driverpack list                                  # le pack du poste sur lequel vous êtes
spokapilote driverpack list --model "Latitude 7420"
spokapilote driverpack list --model "EliteBook 840 G6"
spokapilote driverpack list --model "ThinkPad T14s Gen 2" --os win11
spokapilote driverpack list --model 20WM                     # identifiant exact (SKU Dell, plateforme HP, type Lenovo)
spokapilote driverpack list --model "Latitude 7455" --arch arm64
spokapilote driverpack list --model "Latitude 7420" --all    # toutes les versions publiées, pas seulement la dernière
```

La recherche par nom se fait **par mots entiers** : « ThinkPad T14 » ne renvoie pas les T14s. Le nom de gamme
(Latitude, EliteBook, ThinkPad...) suffit à choisir le bon catalogue ; sinon les trois sont interrogés.
Par défaut, seule la **version la plus récente** est listée pour chaque modèle, Windows (10 / 11) et architecture.

Exemple réel :

```
  Dell
    • Latitude 7420
        Windows 11 x64 · 16KXP version A07 du 08/02/2024 · 1,17 Go · exe
        identifiants : 0A36, 0A37
```

> Certains modèles n'ont pas de pack Windows 11 (ex. EliteBook 840 G6 : dernier pack Windows 10 21H1,
> sp114161). Le pack Windows 10 s'installe en général correctement sur Windows 11, mais validez-le sur un poste.

### Télécharger un pack dans le dépôt

```powershell
spokapilote driverpack download D:\Pilotes --model "Latitude 7420" --os win11
```

Étapes, toutes journalisées dans `%ProgramData%\SpokaPilote\Logs` :

1. téléchargement depuis le serveur officiel, **empreinte du catalogue vérifiée** (fichier supprimé sinon) ;
2. pour un `.exe` : **signature Authenticode valide et éditeur approuvé** (Dell, HP Inc., Lenovo), sinon refus ;
3. **extraction sans installation** (rien n'est installé sur le poste qui télécharge) :
   - Dell `.cab` : `expand.exe` ; Dell `.exe` : `/s /e=<dossier>` ;
   - HP SoftPaq : `/s /e /f <dossier>` ;
   - Lenovo (Inno Setup) : `/VERYSILENT /DIR=<dossier> /EXTRACT=YES` ;
4. **contrôle de chaque INF** : catalogue `.cat` présent et signature valide (le contrôle fin, par périphérique,
   reste fait à l'installation) ;
5. rangement dans `D:\Pilotes\<Fabricant>\<Modèle>\<WinXX-arch>\` avec un fichier `spokapilote-pack.json`
   (origine, version, date, empreintes, signataire, nombre d'INF signés) ; le fichier téléchargé est supprimé
   (`--keep-download` pour le garder : il est alors copié à côté du pack, dans le dossier du modèle du dépôt).

Relancer la commande ne retélécharge rien si le même pack est déjà dans le dépôt (`--force` pour forcer).
Quand le constructeur publie une nouvelle version, elle remplace l'ancienne dans le même dossier.

Points pratiques :

- **Console administrateur recommandée** : les SoftPaq HP exigent l'élévation pour s'extraire (les packs Dell
  s'extraient sans ; les packs Lenovo ont été validés depuis une console administrateur).
- **Chemin du dépôt court** : les packs ont une arborescence profonde. SpokaPilote extrait dans un dossier
  temporaire court puis refuse de publier si un chemin final dépasse 259 caractères. Préférez `D:\Pilotes` ou
  `\\srv\pilotes` à un dossier enfoui dans un profil utilisateur.
- **Espace disque** : un pack extrait occupe jusqu'à 4 fois sa taille (constaté : 484 Mo → 1,98 Go chez Dell,
  890 Mo → 2,98 Go chez HP). SpokaPilote vérifie l'espace libre avant de télécharger.

---

## Microsoft Surface

Les Surface reçoivent l'essentiel de leurs pilotes et firmwares par Windows Update. Pour le déploiement (images,
postes sans réseau, réinstallation propre), Microsoft publie en plus, pour chaque modèle, un **MSI « drivers and
firmware »** cumulatif sur le Centre de téléchargement.

### D'où viennent les informations (sources officielles consultées)

| Information | Source |
|---|---|
| Liste modèle → page de téléchargement | [Manage & deploy Surface driver & firmware updates](https://learn.microsoft.com/surface/manage-surface-driver-and-firmware-updates) (Microsoft Learn) |
| SKU système de chaque modèle | [Surface System SKU reference](https://learn.microsoft.com/surface/surface-system-sku-reference) (Microsoft Learn) |
| Convention de nommage des MSI | même page Learn : `[Produit]_[Win10/Win11]_[build]_[version].[révision].msi` |
| Extraction sans installation | [Enable Surface keyboard for Windows PE deployment](https://learn.microsoft.com/surface/enable-surface-keyboard-for-windows-pe-deployment) : `msiexec.exe /a <msi> targetdir=<dossier> /qn` |
| Lien réel, taille et date du MSI | la page `details.aspx?id=…` du modèle (bloc JSON `__DLCDetails__` → `downloadFile[]`) |

Microsoft ne publie **pas de catalogue machine** comme Dell, HP ou Lenovo. SpokaPilote embarque donc une table
versionnée (`src/SpokaPilote.Core/Repository/surface-driverpacks.json`) qui ne contient que ce qui figure sur les pages
Learn ci-dessus : nom du modèle, identifiant de la page de téléchargement, architecture (Snapdragon / SQ3 = ARM64,
Intel / AMD = x64), « System Model » et « System SKU » SMBIOS. **Les liens des MSI ne sont pas stockés** : ils changent
à chaque mise à jour mensuelle et sont relus sur la page officielle (cache 24 h), puis **relus encore juste avant le
téléchargement**. Un fichier retiré produit un message clair, par exemple :

```
SurfacePro9_Win11_22631_26.091.15297.0.msi n'est plus publié sur la page officielle
https://www.microsoft.com/en-us/download/details.aspx?id=104680 ; version actuelle :
SurfacePro9_Win11_22631_26.101.xxxxx.0.msi. Relancez la commande pour la télécharger.
```

Modèles couverts (37 pages) : Surface Pro 7, 7+, 8, 9 (Intel), 9 5G (SQ3), 10, 10 5G, 11ᵉ édition (Snapdragon et
Intel), 12 pouces 1ʳᵉ et 2ᵉ éditions, 12ᵉ édition (Snapdragon et Intel) ; Surface Laptop 3, 4 (Intel et AMD), 5, 6,
7ᵉ édition (Snapdragon, Intel, 5G Intel), 13 pouces 1ʳᵉ édition (Snapdragon et Intel), 8ᵉ édition (Snapdragon et
Intel), 13 pouces 2ᵉ édition ; Surface Laptop Studio 1 et 2 ; Surface Laptop Go 1, 2, 3 ; Surface Go 2, 3, 4 ;
Surface Book 3 ; Surface Studio 2+. Volontairement absents : les variantes sans page indiquée par Microsoft (Surface
Pro 11ᵉ édition 5G, Surface Pro 5G 12ᵉ édition Intel...) et la Surface Laptop Ultra (architecture non indiquée par
les pages officielles). Pour ajouter un modèle : une entrée dans le JSON, uniquement à partir de ces deux pages Learn.

### Identifier le poste

Sur une Surface, `driverpack list` sans `--model` lit `Win32_ComputerSystem` : fabricant « Microsoft Corporation »,
« System Model » (ex. `Surface Pro 9`) et **« System SKU »** (`SystemSKUNumber`, la même valeur SMBIOS que
`(Get-CimInstance -Namespace root\wmi MS_SystemInformation).SystemSKU` cité par Microsoft), ex.
`Surface_Pro_9_for_Business_2038`. Le SKU départage deux appareils de même « System Model » (Surface Pro 9 Intel /
Surface Pro 9 5G). Si le SKU est absent de la table (variante régionale récente), la recherche se replie sur le
« System Model », l'architecture du poste départageant. Une machine virtuelle Hyper-V (« Microsoft Corporation /
Virtual Machine ») n'est pas prise pour une Surface.

```powershell
spokapilote driverpack list --vendor surface --model "Surface Pro 9"
spokapilote driverpack list --model "Surface Laptop 4"                 # « Surface » suffit à choisir Microsoft
spokapilote driverpack list --model Surface_Laptop_Studio_2_2029       # SKU exact
spokapilote driverpack list --model "Surface Pro 11th Edition" --arch arm64
spokapilote driverpack download D:\Pilotes --model "Surface Go 3" --os win11 --arch x64
```

Exemple réel :

```
  Microsoft
    • Surface Pro 9 (Intel)
        Windows 11 x64 (23H2) build 22631 · SurfacePro9_Win11_22631_26.091.15297.0.msi du 05/10/2026 · 0,84 Go · msi
        identifiants : Surface_Pro_9_2038, Surface_Pro_9_for_Business_2038, Surface Pro 9
        page officielle : https://www.microsoft.com/en-us/download/details.aspx?id=104680
    • Surface Pro 9 with 5G (SQ3)
        Windows 11 ARM64 (23H2) build 22631 · SurfacePro9-5G_Win11_22631_26.062.19189.0.msi du 17/11/2026 · 0,38 Go · msi
```

Une page peut proposer plusieurs MSI (un par build de Windows : 26100 et 26200 pour la Surface Pro 12ᵉ édition
Intel) : le plus récent est retenu, `--all` les liste tous. Les fichiers hors convention de nommage (ex. le MSI du
seul pilote graphique NVIDIA de la Surface Laptop Studio 2) sont ignorés ; les MSI « archivés » listés dans le texte
de la page (anciennes versions de Windows, plus mises à jour) ne sont pas proposés.

### Ce que fait `driverpack download` pour une Surface

1. relecture de la page officielle : lien actuel, taille et date ;
2. téléchargement depuis `download.microsoft.com` ; **taille comparée à celle annoncée par la page** ;
3. **signature Authenticode valide et signataire « Microsoft Corporation » exigés** (Microsoft ne publie aucune
   empreinte : c'est la garantie d'intégrité ; un MSI signé par un autre éditeur, même approuvé, est refusé) ;
4. **extraction sans installation** : `msiexec /a <msi> /qn TARGETDIR=<dossier> /L*v <journal>` (installation
   administrative : copie des fichiers, aucune action personnalisée exécutée, rien n'est installé sur le poste ;
   fonctionne sans droits administrateur). La copie administrative du `.msi` est retirée du dossier ;
5. contrôle du catalogue `.cat` de chaque INF, comptage des firmwares, rangement dans
   `D:\Pilotes\Microsoft\<Modèle>\Win11-<arch>\SurfaceUpdate\<composant>\` avec `spokapilote-pack.json`
   (page d'origine, fichier, taille, signataire, nombre d'INF et de firmwares).

### Pilotes et firmwares

Le MSI contient **les deux** : pilotes (Wi-Fi, audio, graphique, caméras, capteurs, Thunderbolt, chipset...) et
**firmwares** (UEFI, SAM, contrôleur embarqué EC, batterie, clavier, TPM, stations d'accueil, stylet...). Un firmware
est un INF de classe `Firmware` qui déclare un identifiant `UEFI\RES_{GUID}` et livre une capsule `.bin` : une fois
installé, Windows applique la mise à jour du firmware **au redémarrage suivant**. SpokaPilote les distingue partout :

| Où | Comportement |
|---|---|
| `driverpack download` | nombre de firmwares affiché et enregistré (`firmwareInfCount` dans `spokapilote-pack.json`) |
| `install --repo` (dépôt local) | un INF `Firmware` est proposé comme « BIOS / Firmware » : **jamais installé sans `--include-firmware`**, et la politique `allowFirmware: false` le bloque (comme les BIOS Dell / HP) |
| `image inject` | firmwares acceptés comptés à part ; `--no-firmware` les écarte de l'image |

Constaté sur le pack Surface Go 3 (`SurfaceGo3_Win11_22631_26.100.616.0.msi`, 708 Mo) : 93 INF signés dont
13 firmwares, 2,7 Go une fois extrait (le pilote graphique Intel représente à lui seul 1,3 Go).

Conseil Microsoft pour les images : injecter le pack complet (pilotes + firmwares) dans `install.wim` donne un poste
à jour dès le premier démarrage ; pour `boot.wim` (WinPE), injectez seulement le clavier / pavé tactile, le stockage
et le réseau, avec `--no-firmware`.

---

## Scénario : « 30 Latitude 7420 neufs »

Les postes arrivent avec Windows 11 préinstallé ou réinstallé depuis une clé : il manque des pilotes (réseau,
audio, lecteur d'empreintes...). Sans poste de référence :

```powershell
# 1. Une fois, sur votre poste (console administrateur) : le pack officiel Dell dans le dépôt du service
spokapilote driverpack download \\srv\pilotes --model "Latitude 7420" --os win11

# 2. Une fois pour tout le parc (ou fichier settings.json déployé par GPO / Intune)
spokapilote config add-repo \\srv\pilotes

# 3. Sur chacun des 30 postes (script de démarrage, RMM, Intune Win32, clé USB)
spokapilote install --yes --output-dir \\srv\rapports
```

À l'étape 3, la source « Dépôt local » indexe automatiquement les INF du pack (index mis en cache : l'indexation
n'est faite qu'une fois) et propose pour chaque périphérique l'INF qui déclare son identifiant matériel exact,
pour l'architecture et la version de Windows du poste. Chaque INF est revérifié (signature) avant installation.
Ce qui manque est complété depuis le catalogue Dell et Microsoft si le poste a Internet.

Sans réseau du tout (pilote de carte réseau absent), copiez le dossier `\\srv\pilotes\Dell\Latitude_7420` sur une
clé USB avec SpokaPilote portable : `spokapilote install --repo E:\Pilotes --yes`.

Suivi : `spokapilote fleet-report \\srv\rapports` liste les postes qui ont encore un périphérique sans pilote.

---

## 2. Injection de pilotes dans une image Windows

> **Validé en réel (novembre 2026)** sur l'`install.wim` officiel de Windows 11 Entreprise (version d'évaluation,
> build 26300) : ajout puis enregistrement de 106 pilotes (dont le pack Surface Go 3 complet), et injection dans un
> Windows hors ligne appliqué dans un VHDX. Détail en fin de document. Travaillez toujours sur une **copie** de
> l'image et testez l'installation obtenue avant de la diffuser.

Nécessite une **console administrateur** (DISM). Les commandes sont lancées avec `/English` : les messages de DISM
ne dépendent pas de la langue de Windows (les analyseurs comprennent aussi le français).

### Voir le contenu d'une image

```powershell
spokapilote image info E:\sources\install.wim
spokapilote image info E:\sources\install.wim --index 6
```

```
  [6] Windows 11 Professionnel
      x64 · version 10.0.26100.1742 · édition Professional · 5,1 Go · fr-FR (Default)
```

### Injecter un dépôt dans un install.wim

```powershell
spokapilote image inject --wim E:\sources\install.wim --index 6 --repo D:\Pilotes\Dell --dry-run   # contrôle seul
spokapilote image inject --wim E:\sources\install.wim --index 6 --repo D:\Pilotes\Dell
spokapilote image inject --wim E:\sources\install.wim --index 1,6 --repo D:\Pilotes\Dell --repo D:\Pilotes\HP
spokapilote image inject --wim E:\sources\install.wim --index all --repo D:\Pilotes --yes
```

Déroulement :

1. `dism /Get-WimInfo /Index:n` : architecture (x64 / ARM64) et version exacte de l'image ;
2. **contrôle de chaque INF du dépôt** avant toute modification : section de modèles applicable à l'architecture
   et à la version de l'image, catalogue `.cat` présent, signature valide. Les INF écartés sont listés avec le
   motif (« ne convient pas à l'image arm64 », « non signé »...) ;
3. `dism /Mount-Image` dans `%ProgramData%\SpokaPilote\Mount\...` ;
4. `dism /Add-Driver` : un seul appel `/Driver:<dépôt> /Recurse` si tout le dépôt est accepté ; sinon les appels
   sont faits dossier par dossier pour que **DISM ne reçoive jamais un INF écarté** ;
5. `dism /Unmount-Image /Commit`.
   **En cas d'erreur** à n'importe quelle étape : `/Unmount-Image /Discard` (l'image reste telle qu'avant), puis
   `/Cleanup-Mountpoints` si le démontage échoue.

Option `--no-firmware` : écarte les INF de classe `Firmware` (capsules UEFI des packs Surface, par exemple), qui
seraient sinon appliqués au premier démarrage des postes installés depuis l'image.

Résultat : nombre d'INF injectés (dont firmwares), refusés par DISM et écartés au contrôle ; rapport JSON dans
`%ProgramData%\SpokaPilote\Reports\injection-<date>.json`, journal DISM complet dans `Logs\dism-<date>.log`.

Limites de format : une image `.esd` doit d'abord être convertie en `.wim`
(`dism /Export-Image /SourceImageFile:install.esd /SourceIndex:6 /DestinationImageFile:install.wim /Compress:max`) ;
une image fractionnée `.swm` ne peut pas être modifiée ; un fichier copié depuis un DVD/ISO est souvent en lecture
seule (retirez l'attribut). Travaillez toujours sur une **copie** de l'image d'origine.

### Injecter dans un Windows hors ligne

Disque d'un PC qui ne démarre plus branché en `D:`, VHD(X) monté, partition Windows vue depuis WinPE :

```powershell
spokapilote image inject --offline D:\ --repo E:\Pilotes\Dell
```

L'architecture et la version sont lues dans le noyau du Windows ciblé (`D:\Windows\System32\ntoskrnl.exe`).
SpokaPilote refuse de viser le Windows en cours d'exécution (utilisez `spokapilote install`).

### Pas à pas validé : clé d'installation Windows 11 Entreprise + pilotes

```powershell
# Console administrateur. ISO officielle (ici la version d'évaluation du Microsoft Evaluation Center).
$iso = Mount-DiskImage -ImagePath D:\ISO\Win11_Entreprise.iso -PassThru
$src = ($iso | Get-Volume).DriveLetter + ':'
New-Item -ItemType Directory C:\Travail | Out-Null
Copy-Item "$src\sources\install.wim" C:\Travail\install.wim      # install.esd ? voir « Limites de format »
attrib -r C:\Travail\install.wim                                   # copié depuis une ISO : lecture seule
Dismount-DiskImage -ImagePath D:\ISO\Win11_Entreprise.iso

spokapilote image info C:\Travail\install.wim                      # repérer l'index de l'édition
spokapilote driverpack download D:\Pilotes --model "Surface Go 3" --os win11 --arch x64
spokapilote image inject --wim C:\Travail\install.wim --index 1 --repo D:\Pilotes --dry-run
spokapilote image inject --wim C:\Travail\install.wim --index 1 --repo D:\Pilotes --yes

# Contrôle indépendant : l'image enregistrée contient bien les pilotes
New-Item -ItemType Directory C:\Verif | Out-Null
dism /Mount-Image /ImageFile:C:\Travail\install.wim /Index:1 /MountDir:C:\Verif /ReadOnly
dism /Image:C:\Verif /Get-Drivers /Format:Table
dism /Unmount-Image /MountDir:C:\Verif /Discard
```

Durées constatées (machine virtuelle 4 processeurs, 3 Go de mémoire) : montage ≈ 3 min, ajout de 106 pilotes
≈ 3 min 30, enregistrement (`/Commit`) ≈ 7 min ; 13 min 30 au total. Le montage occupe environ la taille
décompressée de l'image (24 Go ici) dans `%ProgramData%\SpokaPilote\Mount`.

### « Error: 87 — An initialization error occurred »

Si **toute** commande DISM hors ligne échoue ainsi (même `dism /Image:<montage> /Get-Drivers` lancé à la main) et que
le journal DISM contient `Failed trying to determine the OS Version. HRESULT=0x80070057`, le poste refuse de charger
les ruches de registre de l'image. Vérification : `reg load HKLM\TEST <dossier>\Windows\System32\config\SOFTWARE`
échoue (constaté : « Nom de fichier ou extension trop long », même pour une ruche minuscule créée par `reg save`).
Cause habituelle : un pilote noyau tiers qui filtre le registre (anti-triche, sécurité). SpokaPilote le signale
dans son message d'erreur ; faites l'injection depuis un autre poste, une machine virtuelle ou WinPE. Ce n'est pas un
problème d'image ni de version de DISM : la même image s'injecte sans erreur sur un poste sain.

### Après une coupure

```powershell
spokapilote image cleanup      # liste les images montées et nettoie les montages orphelins
```

---

## Scénario : « préparer une clé d'installation Windows avec tous les pilotes »

Objectif : une clé USB d'installation Windows 11 qui reconnaît d'emblée le stockage (Intel RST / VMD), le réseau
et le Wi-Fi des Latitude 7420 et des EliteBook du parc.

```powershell
# 1. Packs officiels dans un dépôt « image » (console administrateur)
spokapilote driverpack download D:\PilotesImage --model "Latitude 7420" --os win11
spokapilote driverpack download D:\PilotesImage --model 8549 --vendor hp --os win10   # pas de pack Windows 11 pour ce modèle

# 2. Copiez le contenu de l'ISO Windows 11 dans un dossier de travail, puis repérez l'édition
spokapilote image info C:\Travail\Win11\sources\install.wim

# 3. Pilotes dans l'édition Professionnel (index 6 ici)
spokapilote image inject --wim C:\Travail\Win11\sources\install.wim --index 6 --repo D:\PilotesImage --yes

# 4. Pilotes de stockage et réseau aussi dans l'environnement d'installation (boot.wim, index 2 = Windows Setup)
spokapilote image inject --wim C:\Travail\Win11\sources\boot.wim --index 2 --repo D:\PilotesImage\Dell --yes
```

5. Copiez le dossier de travail sur la clé. Une clé FAT32 n'accepte pas de fichier de plus de 4 Go : fractionnez
   le `install.wim` enrichi (`dism /Split-Image /ImageFile:install.wim /SWMFile:install.swm /FileSize:3800`) ou
   utilisez une clé NTFS / exFAT avec un démarrage UEFI compatible.

Conseils :

- Injecter **tous** les pilotes d'un pack dans `install.wim` grossit l'image (de quelques centaines de Mo à plus
  d'un Go par modèle) ; ne gardez que les modèles réellement déployés.
- Pour `boot.wim`, limitez-vous au stockage, au réseau et aux claviers / pavés tactiles : c'est un WinPE, les
  pilotes audio ou graphiques n'y servent à rien.
- Les pilotes d'une autre architecture sont écartés automatiquement : un dépôt mixte x64 / ARM64 peut être injecté
  tel quel dans une image x64.
- Après l'installation de Windows depuis la clé, `spokapilote install --yes` complète et met à jour le reste.

---

## Ce qui a été validé en réel (novembre 2026, Windows 11 25H2, DISM 10.0.26100)

- `driverpack list` sur les catalogues officiels réels : Dell Latitude 7420, HP EliteBook 840 G6 (plateforme 8549),
  Lenovo ThinkPad T14s Gen 2 (20WM/20WN et 20XF/20XG) et 20XW (ThinkPad X1 Carbon Gen 9).
- `driverpack download` complet (empreinte SHA-256, signature, extraction, contrôle des INF) :
  Dell Pro Micro QCM1255 Win11 (484 Mo, 123 INF signés), HP Engage One Pro AIO Win11 sp170248 (890 Mo, 226 INF
  signés), Lenovo ThinkPad T14s Gen 2 AMD (20XF) Win11 (0,97 Go, 90 INF signés) ; les 438 INF des trois packs
  indexés par `spokapilote scan --repo <dossier> --sources repo`.
- `image info`, montage, contrôle des INF (x64 accepté, ARM64 et non signés écartés), démontage `/Discard` en cas
  d'erreur, `/Cleanup-Mountpoints`, garde-fous `--offline` : sur une copie du WinPE du Windows ADK.
- `driverpack list --vendor surface` sur les pages officielles réelles (Surface Pro 9 Intel / 5G, Laptop 4 Intel /
  AMD, Laptop Studio 2 par SKU, Go 2/3/4, Laptop Go 1/2/3, Pro 11ᵉ édition ARM64) ; `driverpack download` complet
  du pack **Surface Go 3** (`SurfaceGo3_Win11_22631_26.100.616.0.msi`, 708 Mo, taille conforme, signature
  « Microsoft Corporation » valide, `msiexec /a` depuis une console **non administrateur**, 93 INF signés dont
  13 firmwares, 2,7 Go extraits) puis indexation par `spokapilote scan --repo <dossier> --sources repo`.

### Injection validée (26 novembre 2026)

Image : `install.wim` de l'ISO d'évaluation officielle **Windows 11 Entreprise fr-FR** (Microsoft Evaluation Center,
lien `go.microsoft.com/fwlink/?LinkId=2382602` → `software-static.download.prss.microsoft.com`,
`26300.9457.260913-1737.26h2_ge_release_svc_refresh_CLIENTENTERPRISEEVAL_OEMRET_x64FRE_fr-fr.iso`, 8 234 248 192
octets ; aucune empreinte n'est publiée sur la page, `setup.exe` porte une signature Microsoft valide). Une seule
édition : index 1, « Version d'évaluation de Windows 11 Entreprise », x64, 10.0.26300.9457.

Dépôt injecté : 13 pilotes réels exportés d'un poste (`pnputil /export-driver` : Realtek réseau et audio, Intel
chipset et audio, Logitech, WCH) + le pack Surface Go 3 complet = 106 INF signés.

| Étape | Résultat |
|---|---|
| `image info` | index 1, x64, 10.0.26300.9457, EnterpriseEval, 24,3 Go, fr-FR |
| `image inject --wim … --dry-run` | 106 INF acceptés dont 13 firmwares ; avec `--no-firmware` : 93 acceptés, 13 écartés |
| `image inject --wim … --yes` | montage, `/Add-Driver /Driver:<dépôt> /Recurse` : **106 ajoutés, 0 refusé**, `/Unmount-Image /Commit` réussi (13 min 30) |
| contrôle indépendant | image remontée en lecture seule : `dism /Get-Drivers` liste **106 pilotes tiers** (dont les capsules Surface de classe Firmware) |
| `image inject --offline W:\` | image appliquée (`dism /Apply-Image`) dans un VHDX temporaire : **13 pilotes ajoutés, 0 refusé** ; `dism /Image:W:\ /Get-Drivers` en liste 13 |
| `image cleanup` | aucune image montée restante |

Ces étapes ont été exécutées dans une machine virtuelle Hyper-V jetable (Windows 11 Entreprise installé depuis la
même ISO), car le poste de développement refuse tout chargement de ruche de registre hors ligne (voir « Error: 87 »
ci-dessus) : c'est aussi l'explication de l'échec constaté auparavant avec le WinPE de l'ADK. La sortie de DISM est
désormais décodée dans la page de codes OEM de la console (850 en France) : « Version d'évaluation » s'affichait
auparavant « Version d'\uFFFDvaluation ».

Non couvert : injection dans `boot.wim` (même code, non rejoué) et démarrage d'un poste réel installé depuis l'image
enrichie (le contenu de l'image est vérifié, pas l'installation des pilotes sur un matériel Surface).
