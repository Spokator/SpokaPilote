# Packs de pilotes constructeurs et injection dans les images Windows

Deux outils pour les déploiements de parc :

- **`spokapilote driverpack`** : télécharge le *pack complet de pilotes* publié par Dell, HP ou Lenovo pour un
  modèle (les « driver packs » destinés à SCCM / MDT) et le range dans votre dépôt. Plus besoin de préparer un
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

Les packs eux-mêmes sont téléchargés depuis `downloads.dell.com`, `ftp.hp.com` et `download.lenovo.com`
(HTTPS obligatoire, redirections comprises) : aucun hôte supplémentaire n'est nécessaire.

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
   (`--keep-download` pour le garder).

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

> **Fonction expérimentale (v2.0).** Le montage, le contrôle des pilotes et le démontage sont validés ; l'ajout
> effectif des pilotes (`/Add-Driver` puis enregistrement) n'a pas encore été validé sur une vraie image `install.wim`.
> Travaillez toujours sur une **copie** de l'image et testez l'installation obtenue avant de la diffuser.

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

Résultat : nombre d'INF injectés, refusés par DISM et écartés au contrôle ; rapport JSON dans
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
- **Non validé en réel** : l'ajout effectif de pilotes (`/Add-Driver`) puis `/Commit`. Sur le poste de test, DISM
  refuse tout service hors ligne de l'image WinPE 10.0.25398 du Windows ADK (« Error: 87 — An initialization error
  occurred », y compris avec le DISM de l'ADK) ; aucune image `install.wim` de Windows n'était disponible.
  À valider sur un vrai `install.wim` avant diffusion.
