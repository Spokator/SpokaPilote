# SpokaPilote

**Les bons pilotes, pour tout le poste et les imprimantes d'entreprise, depuis les sources officielles uniquement — et le suivi de tout le parc.**

Fini la tournée des sites constructeurs et les « driver updaters » douteux. SpokaPilote inventorie la machine,
identifie chaque composant par son identifiant matériel exact, va chercher le pilote **chez Microsoft, chez le
constructeur du PC ou chez le fabricant de la carte graphique**, le vérifie, puis l'installe. Il configure aussi les
imprimantes et copieurs, s'entretient tout seul sur chaque poste et remonte l'état du parc dans une console web.

| | |
|---|---|
| Poste | Application Windows 11 (thème Fluent, français) + ligne de commande `spokapilote` |
| Parc | Console centrale web auto-hébergée (Docker), politique de pilotes, maintenance planifiée |
| Plateformes | Windows 10 1809+ et Windows 11, **x64 et ARM64** |
| Technologie | .NET 10 LTS, WPF, ASP.NET Core ; aucune dépendance tierce côté poste |
| Distribution | [Installeur](https://github.com/Spokator/SpokaPilote/releases/latest) x64 / ARM64, mise à jour automatique, image Docker `ghcr.io/spokator/spokapilote-server` |

---

## Pourquoi c'est « de confiance »

Une chaîne de contrôle stricte, **non désactivable** :

1. **Sources officielles uniquement** — Windows Update, Microsoft Update Catalog, catalogues Dell / HP / Lenovo,
   NVIDIA, dépôt interne de l'entreprise. Tout autre domaine est refusé (HTTPS obligatoire, redirections comprises).
2. **Empreinte vérifiée** — SHA-1 du catalogue Microsoft, SHA-256 HP/Lenovo, MD5 Dell, MD5 de transfert NVIDIA.
3. **Signature numérique** — vérifiée par `WinVerifyTrust` ; l'éditeur doit être approuvé (Microsoft WHQL, Dell, HP,
   Lenovo, Intel, AMD, NVIDIA, Realtek...). Les catalogues de packs Dell et HP sont eux-mêmes vérifiés.
4. **Compatibilité réelle** — l'INF doit déclarer l'identifiant matériel exact, pour l'architecture et la version de
   Windows du poste (un paquet ARM64 au titre trompeur est refusé sur un PC x64).
5. **Politique de l'entreprise** — liste noire, versions figées, sources autorisées ; une liste de référence de
   pilotes connus pour provoquer des écrans bleus (avis Microsoft) est appliquée par défaut.
6. **Filets de sécurité** — point de restauration, sauvegarde des pilotes, historique et **annulation en un clic** ;
   un pilote Windows intégré qui fonctionne n'est jamais remplacé automatiquement.

## Ce que ça couvre

### Sur chaque poste
| Fonction | Détail |
|---|---|
| **Pilotes manquants et mises à jour** | Dépôt interne → constructeur du PC (Dell, HP, Lenovo) / NVIDIA → Windows Update → Microsoft Update Catalog |
| **Pilotes graphiques** | NVIDIA automatique (branche Studio stable par défaut, pilote graphique seul, sans NVIDIA App) ; Intel et AMD : lien officiel |
| **Diagnostic** | Les pannes matérielles (USB défaillant...) sont distinguées des pilotes manquants, avec une explication |
| **Imprimantes** | Découverte SNMP/IPP, modèle exact, pilote vérifié (WHQL exact, universel constructeur, IPP Windows), file d'impression |
| **Historique** | Chaque intervention est tracée ; annulation = retour aux pilotes précédents ; sauvegardes réinstallables |
| **Maintenance automatique** | Tâche planifiée (SYSTEM) : pilotes manquants, mises à jour ou analyse seule, chaque semaine / jour / au démarrage |

### Pour le parc
| Fonction | Détail |
|---|---|
| **Console centrale** | Les postes envoient leurs rapports ; vue d'ensemble, postes à traiter, matériel sans pilote sur tout le parc, historique |
| **Politique de pilotes** | `driver-policy.json` : bloquer un pilote / une version / un matériel, figer une version, limiter les sources ; diffusée par la console, un partage ou une URL |
| **Packs constructeur** | Le pack de déploiement officiel Dell / HP / Lenovo d'un modèle, vérifié et rangé dans le dépôt : plus besoin de poste de référence |
| **Profils d'imprimantes** | Exporter les imprimantes d'un poste, déployer sur les autres (conditions par sous-réseau ou nom de poste), idempotent |
| **Postes sans réseau** | Pack hors ligne préparé ailleurs à partir du rapport du poste (carte réseau sans pilote) |
| **Images Windows** *(expérimental)* | Injection d'un dépôt de pilotes dans un `install.wim` ou un Windows hors ligne (DISM) |
| **Rapports** | HTML/JSON par poste, rapport de parc consolidé, CSV depuis la console |

## Démarrage rapide

### Installation
Téléchargez l'installeur depuis les [Releases](https://github.com/Spokator/SpokaPilote/releases/latest) :
`SpokaPilote-Setup-<version>.exe` (PC Intel/AMD) ou `SpokaPilote-Setup-<version>-arm64.exe` (Snapdragon, Surface).
Installation dans `Program Files`, menu Démarrer, commande `spokapilote` dans tous les terminaux, mise à jour
automatique. Pour un parc : `SpokaPilote-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

### Interface
**Analyser ce poste** → onglet **Pilotes** → **Installer la sélection** (relance en administrateur proposée).

### Ligne de commande (extraits)
```powershell
spokapilote scan --html rapport.html                    # inventaire + pilotes proposés (sans droits admin)
spokapilote install --yes                               # manquants + mises à jour (console admin)
spokapilote install --missing --exclude NVIDIA --yes    # choisir finement
spokapilote rollback last --yes                         # annuler la dernière intervention
spokapilote driverpack download \\srv\pilotes --model "Latitude 7420"
spokapilote printers export-profile imprimantes.json    # puis : printers deploy imprimantes.json
spokapilote schedule enable --weekly lundi --time 12:30 --mode missing --output-dir \\srv\rapports
spokapilote policy test --policy driver-policy.json     # ce que la politique bloquerait sur ce poste
spokapilote server status                               # connexion à la console centrale
spokapilote help                                        # toutes les commandes
```
Codes de sortie : `0` OK · `1` erreur · `2` échec partiel · `3010` redémarrage requis.

## Déployer et suivre un parc de 100 postes

1. **Console centrale** (facultative mais recommandée) : `docker compose -f server/docker-compose.yml up -d`, derrière
   un reverse proxy HTTPS. Voir [docs/CONSOLE-CENTRALE.md](docs/CONSOLE-CENTRALE.md).
2. **Dépôt de pilotes** sur un partage : pack officiel de chaque modèle (`spokapilote driverpack download`) ou export
   d'un poste de référence (`spokapilote export`). Voir [docs/PACKS-ET-IMAGES.md](docs/PACKS-ET-IMAGES.md).
3. **Configuration commune** déployée par GPO / Intune : `%ProgramData%\SpokaPilote\settings.json` (dépôt, console,
   politique). Voir [docs/POLITIQUE.md](docs/POLITIQUE.md).
4. **Installation silencieuse** de SpokaPilote sur les postes, puis **maintenance planifiée** et **profil
   d'imprimantes**. Voir [docs/IMPRIMANTES-ET-PLANIFICATION.md](docs/IMPRIMANTES-ET-PLANIFICATION.md).
5. **Suivi** : la console liste les postes à traiter et le matériel encore sans pilote ; sans console,
   `spokapilote fleet-report \\srv\rapports`.

Postes sans réseau : `spokapilote scan --json E:\poste.json` sur le poste, `spokapilote pack E:\poste.json E:\pack`
sur un poste connecté, puis `spokapilote install --repo E:\pack --yes`.

## Documentation
- [Console centrale](docs/CONSOLE-CENTRALE.md) — installation Docker, jetons, enrôlement, HTTPS
- [Politique de pilotes](docs/POLITIQUE.md) — format, exemples, diffusion
- [Packs constructeur et images](docs/PACKS-ET-IMAGES.md)
- [Imprimantes et maintenance planifiée](docs/IMPRIMANTES-ET-PLANIFICATION.md)
- [Pilotes graphiques](docs/PILOTES-GRAPHIQUES.md)
- [Publication sur le Microsoft Store](docs/PUBLICATION-STORE.md)

## Architecture

```
src/SpokaPilote.Core/         moteur (aucune dépendance UI)
  Inventory/ Matching/        WMI, analyseur INF, classement des correspondances façon Windows
  Sources/                    Windows Update, Microsoft Update Catalog, dépôt local
    Oem/ Gpu/                 Dell, HP, Lenovo ; NVIDIA, Intel, AMD
  Policy/                     politique de pilotes, liste de référence
  Install/                    téléchargement contrôlé, signatures, pnputil, historique, planification, DISM
  Printers/                   SNMP, IPP, découverte, pilotes, profils
  Repository/                 dépôt, packs constructeur, pack hors ligne, export
  Engine/ Online/             orchestration, rapports, rapport de parc, console, mise à jour
src/SpokaPilote.Cli/          spokapilote.exe (Commands/ : une commande par fichier)
src/SpokaPilote.App/          SpokaPilote.App.exe (WPF .NET 10, thème Fluent)
server/SpokaPilote.Server/    console centrale (ASP.NET Core, SQLite, Docker)
tests/                        ~400 tests (cœur + serveur)
installer/ packaging/ scripts/  Inno Setup x64/ARM64, MSIX Store, build et publication
```

## Construire

```powershell
dotnet test SpokaPilote.sln                           # tests
.\scripts\build-installer.ps1                          # installeurs x64 + ARM64 (contrôle automatique de l'interface)
.\scripts\release.ps1                                  # Release GitHub (notes du CHANGELOG) ; l'image Docker suit via GitHub Actions
```
Version : `Directory.Build.props`. Visuels : `tools\make-assets.ps1`.

## État et limites connues (v2.0)

- **Validé en réel** (Windows 11 25H2) : installeur, mise à jour automatique, installation de pilotes Windows Update,
  installation depuis un dépôt puis annulation, pack hors ligne, packs officiels Dell / HP / Lenovo (téléchargés,
  vérifiés, extraits, indexés), NVIDIA (recherche + téléchargement + signature, en simulation), politique de pilotes,
  console centrale en conteneur Docker, profils d'imprimantes et tâche planifiée (avec une imprimante de test).
- **Pas encore validé sur matériel** : catalogues Dell / HP / Lenovo « par modèle » sur un vrai poste de ces marques,
  vraie imprimante réseau, version ARM64 (compilée et vérifiée, pas exécutée), installation réelle d'un pilote NVIDIA.
- **Expérimental** : injection de pilotes dans une image Windows (l'étape d'ajout n'a pas été validée sur un vrai
  `install.wim`).
- Intel et AMD ne publient pas de source exploitable automatiquement : SpokaPilote donne le lien officiel.
- Les installeurs ne sont pas signés (SmartScreen peut avertir au premier lancement) ; la version Store le serait.

## Feuille de route

- Validation terrain sur parcs Dell, HP et Lenovo ; injection d'images validée sur `install.wim`
- Signature de code des installeurs
- Console : actions à distance (lancer une analyse / installation depuis la console), alertes par e-mail
- Pilotes Microsoft Surface (packs MSI officiels), firmware Thunderbolt / stations d'accueil
- Interface en anglais pour le Microsoft Store international
