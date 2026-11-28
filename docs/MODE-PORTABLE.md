# Mode portable : SpokaPilote sur la clé USB du technicien

La version portable s'exécute depuis une clé USB, **sans installation**, sur n'importe quel PC Windows 10/11 :
le technicien branche sa clé, analyse, installe les pilotes, et repart. Tout ce que SpokaPilote produit
(configuration, journaux, historique, sauvegardes, téléchargements) reste **sur la clé** ; seul ce qu'exige
l'installation des pilotes est écrit sur le poste.

Les contrôles sont les mêmes que la version installée : sources officielles uniquement, empreintes et signatures
vérifiées, politique de pilotes, point de restauration avant installation.

---

## 1. Mise en place

1. Téléchargez depuis la [page des versions](https://github.com/Spokator/SpokaPilote/releases/latest) :
   - `SpokaPilote-Portable-<version>-x64.zip` pour les PC Intel / AMD ;
   - `SpokaPilote-Portable-<version>-arm64.zip` pour les PC ARM64 (Snapdragon, Surface Pro X et suivantes).
2. Extrayez l'archive **à la racine de la clé** : vous obtenez `E:\SpokaPilote\`.
3. Format de clé conseillé : **exFAT ou NTFS**. Les packs constructeurs dépassent souvent 4 Go, la limite d'un
   fichier en FAT32.

Contenu de la clé :

```
E:\SpokaPilote\
  SpokaPilote.App.exe          interface graphique
  spokapilote.exe              ligne de commande
  SpokaPilote.portable         fichier témoin : active le mode portable (ne pas supprimer)
  LISEZMOI.txt                 aide-mémoire
  Depot\                       dépôt de pilotes de la clé (vide au départ)
  Data\                        créé au premier lancement
    settings.json              configuration (partagée par tous les postes)
    ui.json                    préférences d'interface (langue, assistant de bienvenue)
    Cache\  Work\  Reports\  Mises-a-jour\
    Postes\
      PC-COMPTA_3f2a9c1b\      un dossier par poste
        Logs\  History\  Backups\
        policy-cache.json  rollout-decisions.json   politique et décisions de diffusion de CE poste (4.0.1)
```

> Une version installée et une copie portable peuvent coexister sur le même PC : elles ne partagent rien.

---

## 2. Comment SpokaPilote reconnaît le mode portable

Au démarrage, l'emplacement des données est choisi dans cet ordre :

| Priorité | Condition | Données | Préférences d'interface |
|---|---|---|---|
| 1 | Variable d'environnement `SPOKAPILOTE_HOME` définie | ce dossier | `%LocalAppData%\SpokaPilote\ui.json` (dans ce dossier pour une copie portable) |
| 2 | Fichier `SpokaPilote.portable` à côté de l'exécutable | `<dossier de l'exe>\Data` | `Data\ui.json` |
| 3 | Sinon (version installée) | `%ProgramData%\SpokaPilote` | `%LocalAppData%\SpokaPilote\ui.json` |

Le fichier témoin est cherché à côté de l'exécutable, pas dans le dossier courant : la relance en administrateur
(bouton « Relancer en administrateur », invite UAC) retrouve donc la même clé et les mêmes données.

Le mode est toujours visible :

- **interface** : « mode portable » dans la barre de titre, et « Mode portable — données sur E:\SpokaPilote\Data »
  dans la barre d'état (l'info-bulle indique le dossier du poste et le dépôt de la clé) ;
- **ligne de commande** : `spokapilote config show`

```
Mode portable — données sur E:\SpokaPilote\Data
Données de ce poste (journaux, historique, sauvegardes) : E:\SpokaPilote\Data\Postes\PC-COMPTA_3f2a9c1b
Dépôt de la clé (utilisé automatiquement) : E:\SpokaPilote\Depot
Fichier : E:\SpokaPilote\Data\settings.json
```

---

## 3. Une clé, des centaines de postes

Chaque poste a son propre dossier `Data\Postes\<NOM>_<id>` : nom de l'ordinateur suivi des 8 premiers caractères
de son identifiant Windows (MachineGuid). Deux postes clonés qui portent encore le même nom ne partagent donc
jamais leur historique.

| Propre à chaque poste (`Postes\<POSTE>`) | Partagé par tous les postes (`Data`) |
|---|---|
| journaux (`Logs`), historique des interventions (`History`), sauvegardes de pilotes (`Backups`), dernière politique de pilotes et dernières décisions de diffusion reçues de la console (`policy-cache.json`, `rollout-decisions.json`) | configuration, cache des catalogues, rapports (nommés `<POSTE>-<date>.json`) |

**Politique et anneaux de diffusion (4.0.1)** : chaque poste garde sa propre copie hors ligne de la politique et des
décisions de diffusion. Jusqu'à la 4.0.0, ces caches étaient à la racine de `Data`, communs à tous les postes : un poste
de l'anneau pilote y laissait « autorisé », et un poste de l'anneau général hors ligne le réutilisait. Les anciens
fichiers partagés sont supprimés au premier lancement de la 4.0.1 (rien n'est recopié vers un poste).

**Téléchargements** : chaque exécution télécharge et extrait dans son propre dossier `Work\<…>`, supprimé à la fin
(4.0.1) ; un fichier n'est plus réutilisé d'une exécution à l'autre.

---

## 4. Dépôt de pilotes de la clé (hors ligne)

Si le dossier `E:\SpokaPilote\Depot` existe, il est **utilisé automatiquement** comme dépôt de pilotes, en plus des
dépôts configurés, sur tous les postes et quelle que soit la lettre de la clé. Il n'est jamais écrit dans
`settings.json` (la lettre change d'un PC à l'autre).

Remplir le dépôt, depuis un poste connecté à Internet :

```
spokapilote driverpack download             pack officiel Dell / HP / Lenovo / Surface de ce modèle -> Depot
spokapilote driverpack download --model "Latitude 7420" --os win11
spokapilote export                          pilotes d'un poste de référence -> Depot (console administrateur)
spokapilote import pilote.zip               archive téléchargée à la main -> Depot
```

Sans dossier indiqué, ces commandes utilisent le dépôt de la clé. Ensuite, sur n'importe quel poste, même sans
réseau :

```
spokapilote install --missing --sources repo
```

Dépôts supplémentaires : `spokapilote config add-repo \\serveur\pilotes`. En mode portable, un chemin **relatif**
est compris par rapport au dossier de la copie portable : `spokapilote config add-repo Depot-Atelier` désigne
`E:\SpokaPilote\Depot-Atelier`, quelle que soit la lettre de la clé.

---

## 5. Ce qui est écrit sur le poste

Uniquement ce qu'exige l'installation des pilotes :

- le **magasin de pilotes** de Windows (`pnputil`), comme pour toute installation de pilote ;
- le **point de restauration** créé avant l'installation (si la protection du système est active) ;
- les programmes d'installation des constructeurs (installeurs `.exe` / `.msi` signés) et leurs fichiers
  temporaires ; l'auto-extracteur NVIDIA exige un dossier sous `%TEMP%`, supprimé ensuite ;
- injection dans une image Windows (`spokapilote image`) : DISM exige un volume NTFS local, le point de montage est
  donc créé dans `%TEMP%\SpokaPilote\Mount` puis vidé.

Rien dans `%ProgramData%\SpokaPilote`, rien dans `%LocalAppData%\SpokaPilote`, aucune tâche planifiée, aucune
modification du PATH.

### Une clé de confiance

Une clé FAT32/exFAT n'a pas de droits d'accès : quiconque la manipule peut modifier `Data\settings.json` (éditeurs
approuvés, console), l'historique ou le dépôt, et ce que SpokaPilote exécute ensuite en administrateur sur le poste.
Le mode portable est fait pour **la clé personnelle du technicien**, qu'il garde sur lui : ne laissez pas une clé
SpokaPilote en libre-service, ne la branchez pas sur un poste dont vous ne maîtrisez pas les comptes, et chiffrez-la
(BitLocker To Go) si elle contient un `ServerToken`. Pour la même raison, en mode portable le jeton de la console reste
dans `Data\settings.json` (la protection du dossier de données de la version installée ne s'applique pas à la clé).

---

## 6. Retour arrière

L'historique des interventions est sur la clé, dans le dossier du poste. Pour annuler une installation,
**rebranchez la clé sur le même poste** :

```
spokapilote history
spokapilote rollback last           intervention de moins de 30 jours (RollbackLastMaxAgeDays)
spokapilote rollback <id>           intervention désignée explicitement (plus ancienne, ou enregistrée avant la 4.0.1)
spokapilote restore                 réinstalle une sauvegarde de pilotes (Data\Postes\<POSTE>\Backups)
```

L'annulation retire du magasin les paquets ajoutés par l'intervention (`oem42.inf`…). Ces noms sont propres à
chaque poste : SpokaPilote **refuse d'annuler l'intervention d'un autre poste** (identifiant MachineGuid enregistré
avec l'intervention, ou à défaut le nom du poste). Depuis la 4.0.1, seuls les paquets que SpokaPilote a lui-même
ajoutés, et qui n'ont pas changé depuis, sont retirés (voir [OUTILS-TECHNICIEN.md, § 5](OUTILS-TECHNICIEN.md#5-retour-arrière-et-sauvegardes-401)).

Sans la clé, le point de restauration Windows créé avant l'intervention reste disponible
(`rstrui.exe`).

---

## 7. Ce qui n'est pas disponible en mode portable

- **Maintenance planifiée et agent** (`spokapilote schedule enable`, `spokapilote agent enable`, boutons
  correspondants de l'interface) : refusés avec le message « Mode portable : les tâches planifiées … ne sont pas
  disponibles ». La tâche tournerait en SYSTEM et pointerait vers la clé, absente une fois le technicien parti.
  Installez SpokaPilote sur le poste pour ces fonctions. L'exécution immédiate (`spokapilote maintenance`) reste
  possible.
- **Mise à jour automatique** : aucun installeur n'est jamais lancé depuis la clé (voir ci-dessous). Une demande
  de mise à jour envoyée par la console centrale est refusée.

Le **cache de pilotes de la console** (4.0, `UseConsoleCache`) reste facultatif en mode portable : sans console
configurée (`ServerUrl` et `ServerToken` dans `Data\settings.json` de la clé), tout se télécharge directement comme
avant. Avec une console joignable depuis le site visité, la clé en profite (fichiers et catalogues servis par la
console, toujours vérifiés sur le poste) ; si la console ne répond pas, le téléchargement direct prend le relais.
Voir [CONSOLE-CENTRALE.md, § 12](CONSOLE-CENTRALE.md#12-cache-de-pilotes).

---

## 8. Mise à jour de la copie portable

- **Interface** : quand une nouvelle version est publiée, le bandeau « Mode portable : téléchargez la nouvelle
  version » propose « Télécharger sur la clé » (ou « Page de la version »).
- **Ligne de commande** : `spokapilote self-update` (ajoutez `--yes` pour ne pas être interrogé).

L'archive portable est téléchargée depuis la Release GitHub dans `Data\Mises-a-jour`, puis **contrôlée** : taille et
empreinte SHA-256 annoncées par GitHub (une archive sans empreinte publiée est refusée ; un fichier incomplet ou
altéré est supprimé). Rien n'est extrait ni exécuté automatiquement.

Pour finir : fermez SpokaPilote, puis extrayez l'archive **à la racine de la clé, par-dessus l'ancienne version**.
Les dossiers `Data` et `Depot` ne font pas partie de l'archive : configuration, historique et dépôt sont conservés.

---

## 9. Pour les mainteneurs : construction

```
.\scripts\publish.ps1 -Portable                 dist\SpokaPilote-Portable-<version>-x64.zip (+ contrôle de la copie extraite)
.\scripts\publish.ps1 -Arch arm64 -Portable     dist\SpokaPilote-Portable-<version>-arm64.zip
.\scripts\build-installer.ps1                   installeurs + archives portables
.\scripts\release.ps1                           publie le tout sur GitHub
```

L'archive est préparée dans `dist\portable-<arch>\SpokaPilote` (copie de `dist\app-<arch>` + `SpokaPilote.portable`
+ `LISEZMOI.txt` + `Depot\` vide, sources dans `installer\portable\`) : `dist\app-<arch>`, source de l'installeur,
ne reçoit jamais le fichier témoin.

**Contrat de nommage** : le nom d'une archive portable ne contient jamais « Setup » ni « Installer ». Les versions
1.0.x prennent le premier fichier « Setup* » d'une Release pour l'installeur, et les versions actuelles ne retiennent
que des `.exe` (`UpdateChecker.Parse`). Les tests `PortableModeTests` vérifient ce contrat et que les scripts
utilisent le préfixe commun `SpokaPilote-Portable-`.
