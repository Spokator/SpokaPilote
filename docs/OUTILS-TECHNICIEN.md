# Boîte à outils du technicien

Quatre outils pour diagnostiquer et remettre en ordre un poste, sans rien installer :

| Outil | Commande | À quoi ça sert |
|---|---|---|
| **Santé des pilotes** | `spokapilote health` | Une note de A à E et la liste de ce qui ne va pas, du plus grave au moins grave |
| **Réparation guidée** | `spokapilote device ...` | Comprendre pourquoi un périphérique est en erreur et appliquer la bonne action |
| **Nettoyage du magasin** | `spokapilote driverstore ...` | Retirer les anciennes versions de pilotes devenues inutiles |
| **État du BIOS** | `spokapilote health` | Savoir si le BIOS est en retard sur le catalogue Dell / HP / Lenovo, sans jamais le flasher |

Toutes les commandes de lecture (`health`, `device list`, `device info`, `driverstore list`, `driverstore clean --dry-run`)
fonctionnent **sans droits administrateur** et ne modifient rien. Les actions réelles demandent une console
administrateur et une confirmation.

---

## 1. Santé des pilotes

```powershell
spokapilote health                       # analyse complète (interroge les sources officielles, ~1 min)
spokapilote health --quick               # rapide : inventaire + politique, sans recherche de mises à jour
spokapilote health --html sante.html     # rapport HTML avec la carte « Santé des pilotes »
spokapilote health --json                # uniquement le JSON, sur la sortie standard (scripts, RMM)
spokapilote health --json sante.json     # affichage normal + JSON dans un fichier
```

La note apparaît aussi à la fin de `spokapilote scan`, dans le rapport HTML (carte « Santé des pilotes ») et dans le
rapport JSON envoyé à la console centrale (objet `health`).

### Exemple

```
Santé des pilotes : note D (59/100) — À traiter
  ⛔ [critique] Pilote dangereux installé : Intel® Smart Sound Technology (Intel® SST) Audio Controller (−30)
      Version 10.29.0.5152 (Intel Corporation, oem42.inf / intcaudiobus.inf) signalée par la liste de référence
      SpokaPilote (règle « ref-intel-sst-intcaudiobus-5152 ») : écrans bleus signalés par Microsoft... Avis :
      https://learn.microsoft.com/... → Mettez à jour : ... est proposé — spokapilote install --only "..."
  ⚠ [attention] Pilote manquant : Contrôleur de communication simple PCI (−15)
  · [info] Panne matérielle probable : Périphérique USB inconnu (échec de demande de descripteur de périphérique)
```

### Ce qui est vérifié et comment la note est calculée

On part de 100 points :

| Constat (code) | Gravité | Points |
|---|---|---|
| Pilote dangereux **déjà installé** (`known-bad-driver`) | critique | −30 chacun, et la note est **plafonnée à D** (59/100 au mieux) |
| Périphérique sans pilote (`missing-driver`) | attention | −15 chacun |
| Périphérique en erreur (`device-error`) | attention | −15 chacun |
| Pilote non signé (`unsigned-driver`) | attention | −15 chacun |
| Pilote tiers de plus de 5 ans (`old-driver`) | info | −3 chacun, −15 au maximum |
| Mise à jour disponible (`update-available`) | info | −2 chacune, −10 au maximum |
| BIOS obsolète, mise à jour déclarée critique / urgente par le constructeur (`bios-outdated`) | attention | −5 (voir § 4 ci-dessous) |
| BIOS obsolète, autre importance (`bios-outdated`) | info | −2 |
| Panne matérielle (`hardware-fault`) | info | 0 (ce n'est pas un problème de pilote) |
| Périphérique désactivé (`device-disabled`) | info | 0 (souvent volontaire) |

Note : **A** ≥ 90 · **B** ≥ 75 · **C** ≥ 60 · **D** ≥ 40 · **E** en dessous. Un poste sans constat obtient 100/A.

Précisions :
- **Pilote dangereux installé** : les pilotes en place (version, fabricant, ID matériel, nom d'INF) sont comparés aux
  règles « block » de la [liste de référence](POLITIQUE.md) (versions signalées par Microsoft ou le constructeur)
  **et** de la politique de l'organisation. Seules les règles qui désignent une version précise (`versions`,
  `versionMin` / `versionMax`) peuvent signaler un pilote installé : une règle « ne pas installer de pilote NVIDIA »
  ne veut pas dire que le pilote en place est dangereux. Une règle `allow` de l'organisation placée avant lève le
  signalement ; `ignoreReferenceRules` et `useReferenceBlocklist: false` s'appliquent aussi. Pour une règle
  `title`, le nom du périphérique et le nom de l'INF (publié `oemNN.inf` ou d'origine) sont comparés.
  Action proposée : la mise à jour trouvée par l'analyse, sinon l'annulation de l'intervention SpokaPilote qui l'a
  installé (`spokapilote rollback <id>`), sinon le retour au pilote précédent.
- **Pilotes anciens** : seuls les pilotes **tiers** (paquets `oemNN.inf`) sont concernés ; les pilotes intégrés à
  Windows (souvent datés de 2006) sont ignorés, ainsi que les dates fictives antérieures à 2000 (les INF du chipset
  Intel sont volontairement datés du 18/07/1968). Un pilote ancien qui fonctionne n'est pas urgent.
- **Mises à jour** : seules celles autorisées par la politique et installables automatiquement comptent (pas les
  liens Intel / AMD, ni les pilotes optionnels). En mode `--quick`, elles ne sont pas évaluées : la note peut être
  un peu meilleure qu'avec l'analyse complète.

### Format JSON (contrat de la console centrale)

```json
"health": {
  "score": 59,
  "grade": "D",
  "findings": [
    { "severity": "critical", "code": "known-bad-driver", "title": "...", "detail": "...", "deviceId": "PCI\\VEN_8086&..." }
  ]
}
```
`severity` vaut `critical`, `warning` ou `info` ; `deviceId` est absent pour un constat global. L'objet `health` est
facultatif (absent des rapports des versions précédentes).

---

## 2. Réparation guidée d'un périphérique

```powershell
spokapilote device list --problems                 # périphériques en erreur ou désactivés, numérotés
spokapilote device list                            # tout le matériel (--all : périphériques logiciels compris)
spokapilote device info 3                          # fiche complète, diagnostic, actions possibles
spokapilote device repair 3 --dry-run              # ce qui serait fait (commande pnputil), sans rien exécuter
spokapilote device repair 3                        # action recommandée, après confirmation (console admin)
spokapilote device repair 3 --action reinstall     # choisir l'action
```

Un périphérique se désigne par son **n°** (affiché par `device list`, les périphériques en erreur sont numérotés en
premier) ou par son **identifiant** (`USB\VID_046D&PID_C31C\...`, ou un morceau unique de celui-ci).

### Actions selon le code d'erreur (Gestionnaire de périphériques)

| Code | Signification | Action proposée |
|---|---|---|
| 10, 43 | Ne peut pas démarrer / arrêté suite à un problème | **Redémarrer** le périphérique, sinon **réinstaller** |
| 22 | Désactivé | **Réactiver** |
| 28 | Aucun pilote | **Rechercher les modifications matérielles**, puis `spokapilote install --missing` |
| 1, 18, 19 | Mal configuré / à réinstaller / registre | **Réinstaller** |
| 24 | Absent ou mal installé | **Rechercher les modifications matérielles**, sinon réinstaller |
| 31, 39, 52, 3, 32, 37 | Pilote non chargé, corrompu, signature invalide... | **Réinstaller** (52 : remplacer par un pilote signé) |
| 12, 14, 21, 29, 45, 48 | Ressources, redémarrage du PC, BIOS, débranché, bloqué | Conseils uniquement |
| Panne USB matérielle | `USB\DEVICE_DESCRIPTOR_FAILURE`, `RESET_FAILURE`... | **Aucune action logicielle** : conseils physiques |

Ce que font les actions (toutes via `pnputil`, l'outil Microsoft) :
- **Redémarrer** (`restart`) : `pnputil /restart-device` — comme un débranchement / rebranchement.
- **Réactiver** (`enable`) : `pnputil /enable-device`.
- **Réinstaller** (`reinstall`) : `pnputil /remove-device` puis `pnputil /scan-devices` — Windows redétecte le
  périphérique et réinstalle le meilleur pilote présent. Le pilote n'est **pas** supprimé du poste. Non proposée pour
  les périphériques logiciels (qui ne seraient pas redétectés).
- **Rechercher** (`rescan`) : `pnputil /scan-devices`.

Après l'action, l'état du périphérique est relu (WMI) pendant 15 secondes au plus : « fonctionne correctement »,
« le problème persiste » ou « redémarrage nécessaire ». Chaque action réelle est enregistrée dans l'historique
(`spokapilote history`). Ces commandes pnputil existent depuis Windows 10 version 2004 ; sur une version plus
ancienne, SpokaPilote renvoie vers le Gestionnaire de périphériques.

Codes de sortie de `device repair` : `0` résolu (ou simulation) · `2` le problème persiste · `3010` redémarrage
nécessaire · `1` refusé ou échec.

### Périphériques critiques

Pour ces périphériques, **`--yes` ne suffit pas** : il faut répondre « o » à la question, ou ajouter
`--confirm-critical` (scripts). Sans confirmation, l'action est refusée, même en simulation dans l'API.

| Périphérique | Risque |
|---|---|
| Disques, contrôleurs de disque et de stockage (DiskDrive, HDC, SCSIAdapter), volumes | Le disque système peut en dépendre |
| Processeur, ordinateur, périphériques système (bus PCI, ACPI, chipset), firmware | Blocage du poste |
| Carte graphique (Display) | L'écran peut s'éteindre ou clignoter |
| Carte réseau (Net) | Coupe le réseau **et la session à distance en cours** (RDP, prise de main) |
| Puce TPM (SecurityDevices) | BitLocker et Windows Hello en dépendent |
| Contrôleurs USB et concentrateurs USB racine | Tous les périphériques USB, clavier et souris compris, sont déconnectés |
| Clavier ou souris, **s'ils sont les seuls en état de marche** | Perte de la main sur le poste |

De plus, pnputil est toujours appelé **sans `/force`** : Windows refuse lui-même d'agir sur un périphérique qui
fournit une fonction système critique.

### Exemple : USB en panne matérielle

```
spokapilote device info 1
n°1 — Périphérique USB inconnu (échec de demande de descripteur de périphérique)
  État          : ⚡ Arrêté suite à un problème signalé (code 43)
  ID matériels  : USB\DEVICE_DESCRIPTOR_FAILURE
Diagnostic
  ... C'est une panne matérielle ou électrique : aucun pilote ni aucune action logicielle ne peut la corriger.
Conseils (panne matérielle : aucune action logicielle)
  • Débranchez le périphérique, attendez 10 secondes et rebranchez-le sur un autre port USB...
```
`spokapilote device repair 1 --action restart` est refusé : rien ne sert de toucher au logiciel.

---

## 3. Nettoyage du magasin de pilotes

Chaque mise à jour de pilote laisse l'ancienne version dans le magasin de Windows
(`C:\Windows\System32\DriverStore\FileRepository`). Sur un poste ancien, cela peut représenter des centaines de Mo,
voire plusieurs Go (pilotes graphiques).

```powershell
spokapilote driverstore list                 # tous les paquets tiers : INF d'origine, fournisseur, classe, version, date, taille, état
spokapilote driverstore list --all           # avec la raison de chaque décision
spokapilote driverstore clean --dry-run      # aperçu : paquets supprimables et espace libérable, rien n'est supprimé
spokapilote driverstore clean                # aperçu puis confirmation (console administrateur)
spokapilote driverstore clean --yes --backup # sans question, avec une copie de chaque paquet avant suppression
```

### Ce qui est supprimé — et ce qui ne l'est jamais

Un paquet n'est proposé que s'il est une **ancienne version inutilisée** d'un pilote (même INF d'origine **et** même
fournisseur) **remplacée** par une version plus récente présente dans le magasin. Ne sont **jamais** proposés :

- un paquet **utilisé** par un périphérique, branché ou non (trois vérifications croisées : pilotes installés,
  configuration enregistrée des périphériques déconnectés, et `pnputil /enum-drivers /devices` qui voit aussi les
  INF d'extension ; sans cette dernière commande, les INF d'extension sont conservés) ;
- la **seule version** d'un pilote, ni la **plus récente** (en cas d'égalité de version et de date, aucune) ;
- une version dont le matériel n'est **pas entièrement pris en charge** par les versions plus récentes (ID matériels
  comparés) ;
- un pilote d'**imprimante** : le spouleur les utilise sans périphérique visible, à traiter à la main dans « Gestion de
  l'impression » ;
- pour les classes **sensibles** (affichage, réseau, stockage, système, firmware, Bluetooth), la **version
  précédente** est conservée pour garder le bouton « Restaurer le pilote » du Gestionnaire de périphériques ;
- les versions antérieures à un paquet installé par une **intervention SpokaPilote encore annulable** (l'annulation
  `spokapilote rollback` en a besoin) ;
- tout paquet dont l'INF ou la version est illisible.

La suppression utilise `pnputil /delete-driver oemNN.inf` **sans `/force` ni `/uninstall`** : si un paquet est
utilisé, Windows refuse. L'utilisation est revérifiée juste avant de supprimer. L'opération est enregistrée dans
l'historique (`spokapilote history`, type « nettoyage du magasin de pilotes »).

### Précautions

- Une suppression **ne s'annule pas** avec `spokapilote rollback`. Avec `--backup`, chaque paquet est d'abord copié
  dans `%ProgramData%\SpokaPilote\Backups\driverstore-<date>` et se réinstalle avec
  `spokapilote restore <dossier>`.
- Faites d'abord `--dry-run`, et nettoyez de préférence après avoir vérifié que les pilotes récents fonctionnent
  (quelques jours après une mise à jour).
- Le gain dépend du poste : quelques dizaines de Mo sur un poste récent, beaucoup plus là où les pilotes graphiques
  ou constructeur ont été mis à jour souvent.

---

## 4. État du BIOS et des firmwares

Un BIOS en retard laisse souvent des failles de sécurité ouvertes. SpokaPilote **signale** un BIOS obsolète, mais ne
le **flashe jamais par surprise** : l'installation reste soumise à `--include-firmware` (réglage « Proposer aussi les
mises à jour de BIOS et de firmware », désactivé par défaut), exactement comme avant.

```
spokapilote health                 # ligne « BIOS : ... » après les constats
spokapilote scan --sources oem     # même ligne dans la synthèse de l'analyse
```
```
BIOS : 1.12.0 installé, 1.18.0 disponible (critique) → https://www.dell.com/support/home/.../DriversDetails?driverId=...
  ⚠ Avant de mettre à jour le BIOS : secteur branché (batterie chargée), BitLocker suspendu (Suspend-BitLocker ...
```

L'état apparaît aussi dans le rapport HTML (carte « BIOS et firmwares »), sur la page **Accueil** de l'interface
(ligne « BIOS » sous les points importants, avec un bouton vers la page officielle) et dans le rapport JSON (objet `firmware`).

### Comment c'est vérifié

Quand la source du constructeur est interrogée (Dell, HP ou Lenovo, catalogue officiel déjà utilisé pour les pilotes),
l'entrée BIOS du **modèle exact** (SKU Dell, plateforme HP, type-machine Lenovo) est **toujours** lue, même sans
`--include-firmware`, puis comparée au BIOS installé (`Win32_BIOS` : version SMBIOS et date de publication) :

| Constructeur | BIOS installé (SMBIOS) | Catalogue | Règle de comparaison |
|---|---|---|---|
| Dell | `1.23.0` (anciens : `A12`) | `vendorVersion` du composant `BIOS` | numérique partie par partie (`1.9.0` < `1.23.0`) ; `A12` ne se compare qu'à `Axx` |
| HP | `T37 Ver. 01.15.00` (grand public : `F.60`) | `<Version>` du SoftPaq de catégorie `BIOS` | numérique ; révision du SoftPaq ignorée (`01.17.00 Rev.A` = `01.17.00`) ; famille `T37` comparée à celle de l'intitulé `(T37)` si présente ; `F.60` ne se compare qu'à `F.xx` |
| Lenovo | `N2XET39W (1.29 )` | paquet de type 3 (BIOS) : niveaux `_Bios` / `Level` (`N2XET41W^`) et attribut `version` | l'identifiant de build fait foi s'il existe des deux côtés (même famille `N2XET` obligatoire), sinon la version entre parenthèses |

Principe : **dans le doute, aucune conclusion**. Formats différents (`A12` contre `1.2.0`), famille de BIOS différente,
version illisible, ou version « plus récente » au catalogue alors que le BIOS installé est daté de plus de 60 jours
après elle : l'état est « impossible de comparer », sans constat ni pénalité, avec la raison.

| État (`firmware.bios.state`) | Signification | Constat de santé |
|---|---|---|
| `outdated` | une version plus récente est publiée pour ce modèle | `bios-outdated` |
| `upToDate` | le BIOS installé est la dernière version | aucun |
| `installedNewer` | le BIOS installé est plus récent que le catalogue (catalogue en retard) | aucun |
| `unknown` | comparaison impossible de façon fiable (raison dans `reason`) | aucun |
| `notInCatalog` | aucun BIOS pour ce modèle dans le catalogue | aucun |

Sans catalogue lu (poste d'une autre marque, source non interrogée ou indisponible), la ligne indique
« non vérifié » et l'objet `firmware` est absent du rapport.

**Importance** : celle déclarée par le constructeur — Dell `<Criticality>` (« Urgent » → critique, « Recommended »,
« Optional »), HP `<ReleaseType>` (« Critical » → critique, « Recommended », « Routine »), Lenovo `<Severity type>`
(1 = critique, 2 = recommandée, 3 = facultative). Si une version **intermédiaire** entre le BIOS installé et la
dernière est critique, la mise à jour est considérée critique. Constat `bios-outdated` : **attention (−5)** si
critique, **info (−2)** sinon. Avec `--include-firmware`, le candidat « BIOS / Firmware » n'est pas compté une
deuxième fois en `update-available`, et il n'est plus proposé si le BIOS installé est déjà à jour (comparaison fiable).

**Liens** : seuls des liens HTTPS vers des domaines officiels sont publiés (hôtes de téléchargement de la politique de
confiance, et pages d'information `www.dell.com`, `support.hp.com`, `pcsupport.lenovo.com`, `support.lenovo.com`) ;
un lien `http://` du catalogue est réécrit en HTTPS.

### Avant de mettre à jour un BIOS

SpokaPilote **ne suspend pas BitLocker** : son installation de BIOS (avec `--include-firmware`) exécute l'outil
officiel du constructeur, sans autre préparation. À faire avant :
- brancher le **secteur** (batterie chargée pour un portable) et ne pas éteindre pendant l'opération ;
- **suspendre BitLocker** pour un redémarrage : `Suspend-BitLocker -MountPoint C: -RebootCount 1` (console
  administrateur), sinon la clé de récupération sera demandée au redémarrage ;
- de préférence, déployer le BIOS par l'outil du constructeur ou votre outil de gestion de parc ; la politique
  `allowFirmware: false` interdit toute installation de BIOS / firmware par SpokaPilote ([POLITIQUE.md](POLITIQUE.md)).

Ce rappel est affiché par `spokapilote health`, par `install` quand la sélection contient un BIOS / firmware, et par
l'interface avant l'installation.

### Stations d'accueil et Thunderbolt

Les firmwares de stations d'accueil (Dell « Docks/Stands », HP « Dock », Lenovo) et de contrôleurs Thunderbolt du
catalogue du modèle sont listés **pour information** (jamais installés par cette fonction) uniquement si les
**identifiants matériels** indiqués par le catalogue (Dell `PCIInfo`, HP section `<Devices>`, Lenovo `_PnPID`)
correspondent à un périphérique présent. Une entrée **sans identifiant matériel** (cas fréquent des stations Dell
WD19 / WD22, reconnues par Dell Command | Update par d'autres moyens) n'est **pas** listée : deviner d'après le nom
produirait des faux positifs. Le rapport le signale dans `firmware.notes`. La version du firmware en place n'étant pas
lisible de façon fiable, aucune comparaison n'est faite.

### Format JSON (objet facultatif `firmware` du rapport d'analyse)

```json
"system": { "biosVersion": "1.12.0", "biosReleaseDate": "2025-02-01T00:00:00", ... },
"firmware": {
  "bios": {
    "vendor": "Dell", "state": "outdated",
    "installedVersion": "1.12.0", "installedDate": "2025-02-01T00:00:00",
    "latestVersion": "1.18.0", "latestDate": "2026-09-15T04:42:33Z",
    "title": "Dell Latitude 7420/7520 System BIOS",
    "importance": "critical", "importanceLabel": "Urgent",
    "downloadUrl": "https://downloads.dell.com/...", "infoUrl": "https://www.dell.com/support/home/..."
  },
  "components": [
    { "kind": "thunderbolt", "title": "...", "version": "37.0", "importance": "recommended", "devices": [ "..." ], "deviceIds": [ "PCI\\VEN_8086&..." ] }
  ],
  "notes": [ "1 firmware(s) de station d'accueil / Thunderbolt du catalogue Dell sans identifiant matériel : non listé(s)..." ]
}
```
`state` : `upToDate`, `outdated`, `installedNewer`, `unknown`, `notInCatalog` ; `importance` : `critical`,
`recommended`, `optional`, `unknown` ; `reason` explique un état `unknown` ou `notInCatalog`. L'objet `firmware` et
`biosReleaseDate` sont absents des rapports des versions précédentes, qui se relisent sans changement ; le contrat
`health` ne change pas (seul le code `bios-outdated` s'ajoute, sans `deviceId`).

### Limites

- Postes Dell, HP et Lenovo seulement (catalogues officiels déjà utilisés pour les pilotes). Microsoft Surface : les
  firmwares UEFI arrivent par les packs pilotes et firmwares (voir [PACKS-ET-IMAGES.md](PACKS-ET-IMAGES.md)), sans
  comparaison de version du BIOS.
- HP : la plateforme doit avoir une référence HP Image Assistant ; Lenovo : seuls les modèles ayant un catalogue par
  type-machine (gamme professionnelle) sont couverts. Les BIOS grand public Lenovo (`GGCN51WW`) ne sont pas comparés.

---

## 5. Retour arrière et sauvegardes (4.0.1)

### Ce que l'annulation retire

`spokapilote rollback` retire du magasin les paquets **que SpokaPilote a lui-même ajoutés**, et seulement s'ils sont
toujours ceux qu'il a installés :

- à l'installation, SpokaPilote enregistre pour chaque paquet le nom publié renvoyé par `pnputil /add-driver`
  (`oem42.inf`), son INF d'origine, l'éditeur, la classe, la version et le dossier du magasin
  (`e1d.inf_amd64_0123abcd`, dont le suffixe dépend du contenu du paquet). Un paquet « déjà présent » n'est pas
  attribué à l'intervention. Pour un installeur constructeur ou Windows Update (aucun nom renvoyé), seuls les paquets
  apparus **pendant cette étape** sont attribués — jamais ceux qu'ajoutent Windows Update, Intune ou un autre outil
  entre deux étapes ;
- à l'annulation, chaque paquet est relu dans le magasin : un nom `oemNN.inf` réattribué depuis à un autre pilote, ou
  un pilote mis à jour entre-temps, est **laissé en place** et signalé ;
- `pnputil /delete-driver oemNN.inf /uninstall`, **sans `/force`** : un paquet que Windows ne peut pas libérer
  (périphérique en service) reste en place, il est signalé et l'intervention reste annulable (seuls les paquets
  restants seront retentés).

| Commande | Limite |
|---|---|
| `spokapilote rollback last`, tâche distante `rollback-last` | intervention de moins de `RollbackLastMaxAgeDays` jours (30 par défaut) ; refusée au-delà, avec l'identifiant à utiliser |
| `spokapilote rollback <id>`, bouton **Annuler** de l'historique | sans limite d'âge, avec les mêmes vérifications |

Une intervention enregistrée **avant la 4.0.1** (noms `oemNN.inf` seuls) ne s'annule que par son identifiant
(`rollback <id>`, ou l'interface) : seuls les paquets encore présents dont le fichier `oemNN.inf` a été créé pendant
l'intervention sont retirés.

### Sauvegardes des pilotes

Avant chaque installation, `pnputil /export-driver *` copie tous les pilotes tiers (1 à 4 Go) dans
`%ProgramData%\SpokaPilote\Backups\<date>`. Depuis la 4.0.1 :

- **pas de nouvelle copie si le magasin n'a pas changé** depuis la dernière sauvegarde complète (même liste de
  `oemNN.inf`, mêmes tailles et dates) : la sauvegarde existante est réutilisée ;
- **rétention** : les `BackupRetentionCount` dernières (3 par défaut), puis les plus anciennes retirées tant que
  l'ensemble dépasse `BackupMaxTotalSizeMB` (10 240 Mo par défaut, 0 = sans limite). La plus récente et celle de la
  dernière intervention encore annulable ne sont jamais supprimées. Les sauvegardes du nettoyage du magasin
  (`driverstore-<date>`, paquets retirés seulement) ne sont pas concernées ;
- les téléchargements et extractions se font dans un dossier propre à chaque exécution (`Work\<…>`), supprimé à la
  fin, réussite ou échec ; les dossiers abandonnés (processus interrompu) sont supprimés après 2 jours ou au-delà de
  10 Go. Les anciens dossiers `Downloads` et `Extracted` (4.0.0) sont supprimés au premier démarrage élevé.

```powershell
spokapilote config set BackupRetentionCount 5
spokapilote config set BackupMaxTotalSizeMB 20480
spokapilote config set RollbackLastMaxAgeDays 14
```

---

## 6. Dossier de données et droits (4.0.1, 4.0.2)

Les tâches planifiées et l'agent s'exécutent en **SYSTEM** : ce qu'ils lisent et exécutent dans
`%ProgramData%\SpokaPilote` doit être hors de portée des utilisateurs du poste (postes partagés, RDS). Jusqu'à la
4.0.0, le dossier héritait des droits par défaut de `%ProgramData%` (les utilisateurs pouvaient y créer des fichiers :
DLL déposée à côté d'un installeur, fichier téléchargé remplacé entre sa vérification et son exécution,
`settings.json` créé d'avance, catalogue en cache daté dans le futur).

Disposition appliquée par l'installeur (`spokapilote secure-data`) puis vérifiée à **chaque démarrage élevé** (contrôle
rapide ; reprise complète si les droits ont été modifiés) :

```
%ProgramData%\SpokaPilote          propriétaire Administrateurs, héritage de ProgramData coupé
  SYSTEM et Administrateurs : contrôle total ; Utilisateurs : lecture et exécution ; aucun CREATOR OWNER
  settings.json  Logs\  History\  Backups\  Reports\       hérités (lisibles par l'interface non élevée)
  server-token.dat                                     jeton de la console : SYSTEM et Administrateurs seulement
  Cache\  Work\  Mount\  Quarantaine\                     SYSTEM et Administrateurs seulement
```

- **Propriétaires reconnus (4.0.2)** : SYSTEM, le groupe Administrateurs, TrustedInstaller, et **tout compte membre du
  groupe local Administrateurs** — membre direct (`PC-PIERRE\Pierre`), administrateur qui exécute SpokaPilote élevé,
  ou membre par un groupe de domaine imbriqué (« Admins du domaine » ; vérifié auprès du domaine, non reconnu s'il est
  injoignable). Utile sur un poste où l'UAC ne demande rien, avec la stratégie « propriétaire par défaut = créateur de
  l'objet », ou pour un fichier copié par le compte d'un technicien. Jamais reconnus : Utilisateurs, Utilisateurs
  authentifiés, Tout le monde, INTERACTIF, CREATOR OWNER, Invité, Utilisateurs du domaine, ni un compte qui n'est pas
  administrateur. Un élément d'un compte administrateur est conservé et son propriétaire ramené aux Administrateurs.
- **Éléments suspects** : tout ce qui n'appartient pas à un propriétaire reconnu est repris puis supprimé (cache,
  téléchargements) ou déplacé dans `Quarantaine\<date>` (configuration, historique, sauvegardes, journaux) — jamais
  utilisé. La quarantaine est purgée après 90 jours. Les jonctions et liens symboliques sont retirés sans être suivis. Avant tout usage, une
  exécution élevée refuse un `settings.json`, une entrée d'historique, une sauvegarde ou un fichier en cache qui
  n'appartient pas aux administrateurs — ou dont **un des dossiers**, depuis `%ProgramData%\SpokaPilote`, est un lien
  ou appartient à un autre compte (jonction `Cache\catalog` créée plus tard) —, et un fichier en cache daté dans le
  futur est périmé.
- **Descripteurs ouverts avant la protection** : un fichier ou dossier ouvert par un utilisateur sous la 4.0.0 garde
  les droits obtenus à son ouverture. À chaque reprise complète, `Cache`, `Work` (et les anciens `Downloads`,
  `Extracted`) sont donc renommés, supprimés sans suivre les liens et recréés neufs : un tel descripteur ne désigne plus
  qu'un dossier supprimé. Si un fichier y est encore ouvert, le renommage échoue : c'est écrit dans le journal, le
  contenu est contrôlé élément par élément, et la reprise complète recommence au démarrage élevé suivant.
- **Images montées (`Mount`)** : rien sous `Mount` n'est jamais contrôlé, repris, réinitialisé ni supprimé (une image
  montée par DISM serait endommagée) ; seuls les droits du dossier `Mount` lui-même sont posés, sans propagation. Il
  n'est renouvelé que s'il est vide.
- **`SPOKAPILOTE_HOME` sur FAT32 / exFAT ou partage réseau** : pas de propriétaires ni d'ACL fiables ; l'emplacement est
  traité comme une clé portable (rien n'y est modifié ni mis en quarantaine, jeton dans `settings.json`), avec un
  avertissement à chaque démarrage élevé. Un dossier existant n'est protégé que s'il est vide, déjà protégé, ou ne
  contient que des éléments de SpokaPilote dont `settings.json` : un dossier de l'utilisateur qui contient par hasard
  `Logs` ou `Reports` n'est jamais repris.
- **Jeton de la console** : hors de `settings.json`, dans `server-token.dat` (DPAPI « machine » + droits réservés aux
  administrateurs). Un `serverToken` en clair dans `settings.json` (4.0.0, fichier déployé par GPO / Intune, kit
  `deploy\Install-SpokaPilote.ps1` antérieur) est déplacé et retiré du fichier **à chaque démarrage élevé** ; le kit
  4.0.1 et `spokapilote config set ServerToken -` (jeton lu sur l'entrée standard, absent de la ligne de commande)
  l'enregistrent directement, sans jamais l'écrire dans `settings.json`. C'est la DACL qui le protège des
  utilisateurs du poste (DPAPI « machine » est déchiffrable par tout compte local) ; une copie du fichier seul est
  inexploitable, pas une image disque complète. Un jeton indéchiffrable (poste cloné depuis une image qui le
  contenait) est signalé dans le journal, supprimé, et le poste continue sans console jusqu'au redéploiement. Le jeton
  n'est jamais affiché (`spokapilote config` : « (défini, …) » ; Paramètres : champ masqué) ; le désinstalleur le
  supprime toujours.
- **Interface ou console non élevée** : elle lit la configuration commune mais écrit ses journaux, rapports, cache et
  dossiers de travail dans `%LocalAppData%\SpokaPilote` ; elle ne lit pas le jeton (l'envoi à la console est fait par
  les exécutions administrateur et les tâches SYSTEM). Elle affiche les blocages et retenues de l'organisation d'après
  la dernière politique reçue par les exécutions SYSTEM du poste (`policy-cache.json`, `rollout-decisions.json`, lus
  seulement s'ils ont été écrits par un administrateur), et le signale : la politique à jour est revérifiée, avec les
  droits administrateur, au moment d'installer. Modifier les paramètres demande l'élévation.
- **Fichiers déposés à la main** : déposez `settings.json` depuis un compte administrateur, un script SYSTEM (Intune,
  GPO) ou `spokapilote config set`. Un fichier copié par le compte d'un administrateur est reconnu (4.0.2) ; pour un
  compte du domaine imbriqué sur un poste souvent hors ligne, donnez-le aux administrateurs :
  `icacls settings.json /setowner *S-1-5-32-544`.
- **Configuration mise en quarantaine (4.0.2)** : un `settings.json` (ou `server-token.dat`) écarté n'est jamais perdu
  en silence : ERREUR dans le journal, marqueur `quarantine.json` (sans secret), avertissement au début de **chaque
  commande** de la CLI et bandeau dans l'interface (Accueil, Paramètres) : « Configuration mise en quarantaine le …
  (fichier créé par un compte non administrateur : …) : vérifiez-la puis `spokapilote config restore` ».
  `spokapilote config restore [<chemin>]` (console administrateur) affiche le fichier (jeton masqué, réglages qui
  étendent la confiance signalés), le restaure après confirmation (`--yes` pour un script ; l'ancien fichier est gardé
  en `settings.json.avant-restauration`), déplace un jeton en clair vers `server-token.dat` et efface le marqueur ;
  `--discard` écarte la configuration (le bandeau disparaît). Au démarrage élevé, sans `settings.json`, une configuration
  en quarantaine est restaurée **automatiquement** si son propriétaire d'origine est désormais un administrateur reconnu.
- **Postes passés par la 4.0.1** : elle a mis en quarantaine les fichiers des comptes administrateurs et ramené leur
  propriétaire aux Administrateurs avant de les déplacer (le propriétaire d'origine est perdu). Au premier démarrage
  élevé de la 4.0.2, sans `settings.json`, le plus récent `Quarantaine\<date>\settings.json` est restauré
  automatiquement (jeton migré) **s'il ne contient aucun réglage qui étend la confiance** (`ExtraTrustedPublishers`,
  `ExtraTrustedHosts`, `RepositoryPaths`, `PolicySource`, `PrinterCatalogUrl`) ; sinon il est signalé comme ci-dessus et
  attend `spokapilote config restore`. Les journaux et rapports mis en quarantaine y restent jusqu'à la purge.
- `spokapilote secure-data --force` refait la reprise complète et en affiche le détail.

---

## Pour l'intégration (interface, scripts)

API du cœur (`SpokaPilote.Core`), sans dépendance à l'interface :

| Besoin | API |
|---|---|
| Santé | `ScanResult.Health` (calculée par `DriverScanEngine.ScanAsync`), ou `DriverHealth.Compute(devices, candidates, policy, options)` → `HealthReport` (`Score`, `Grade`, `GradeLabel`, `Summary`, `Findings`) |
| Réparation | `DeviceRepair.Plan(device, allDevices)` → `RepairPlan` (`Diagnosis`, `Advice`, `Actions`, `IsCritical`, `CriticalReason`) ; `DeviceRepair.ExecuteAsync(plan, action, new RepairOptions { DryRun, ConfirmCritical })` → `RepairResult` |
| BIOS et firmwares | `ScanResult.Firmware` → `FirmwareStatus` (`Bios` : `State`, `InstalledVersion`, `LatestVersion`, `Importance`, `Link`, `Summary` ; `Components` ; `Notes`) ; `FirmwareStatus.Describe(status, system)` (ligne « BIOS : ... ») ; `BiosVersionComparer.Compare(vendor, installed, catalogVersion, buildIds, title)` ; `BiosStatus.UpdateWarning` |
| Magasin | `DriverStoreCleaner.AnalyzeThisPc()` → `DriverStoreAnalysis` (`Entries`, `Candidates`, `ReclaimableBytes`, `Summary`) ; `DriverStoreCleaner.CleanAsync(analysis.Candidates, new DriverStoreCleanOptions { DryRun, Backup })` |
