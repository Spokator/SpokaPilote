# Boîte à outils du technicien

Trois outils pour diagnostiquer et remettre en ordre un poste, sans rien installer :

| Outil | Commande | À quoi ça sert |
|---|---|---|
| **Santé des pilotes** | `spokapilote health` | Une note de A à E et la liste de ce qui ne va pas, du plus grave au moins grave |
| **Réparation guidée** | `spokapilote device ...` | Comprendre pourquoi un périphérique est en erreur et appliquer la bonne action |
| **Nettoyage du magasin** | `spokapilote driverstore ...` | Retirer les anciennes versions de pilotes devenues inutiles |

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

## Pour l'intégration (interface, scripts)

API du cœur (`SpokaPilote.Core`), sans dépendance à l'interface :

| Besoin | API |
|---|---|
| Santé | `ScanResult.Health` (calculée par `DriverScanEngine.ScanAsync`), ou `DriverHealth.Compute(devices, candidates, policy, options)` → `HealthReport` (`Score`, `Grade`, `GradeLabel`, `Summary`, `Findings`) |
| Réparation | `DeviceRepair.Plan(device, allDevices)` → `RepairPlan` (`Diagnosis`, `Advice`, `Actions`, `IsCritical`, `CriticalReason`) ; `DeviceRepair.ExecuteAsync(plan, action, new RepairOptions { DryRun, ConfirmCritical })` → `RepairResult` |
| Magasin | `DriverStoreCleaner.AnalyzeThisPc()` → `DriverStoreAnalysis` (`Entries`, `Candidates`, `ReclaimableBytes`, `Summary`) ; `DriverStoreCleaner.CleanAsync(analysis.Candidates, new DriverStoreCleanOptions { DryRun, Backup })` |
