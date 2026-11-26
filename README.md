# SpokaPilote

[![CI](https://github.com/Spokator/SpokaPilote/actions/workflows/ci.yml/badge.svg)](https://github.com/Spokator/SpokaPilote/actions/workflows/ci.yml)
[![Version](https://img.shields.io/github/v/release/Spokator/SpokaPilote?label=version)](https://github.com/Spokator/SpokaPilote/releases/latest)
[![Licence MIT](https://img.shields.io/github/license/Spokator/SpokaPilote?label=licence)](LICENSE)
[![Windows 10/11 x64 | ARM64](https://img.shields.io/badge/Windows-10%20%7C%2011%20%C2%B7%20x64%20%7C%20ARM64-0B63CE)](https://github.com/Spokator/SpokaPilote/releases/latest)
[![Site web](https://img.shields.io/badge/site-spokator.github.io-14B8A6)](https://spokator.github.io/SpokaPilote/)
[![Console Docker](https://img.shields.io/badge/ghcr.io-spokapilote--server-2496ED?logo=docker&logoColor=white)](https://github.com/Spokator/SpokaPilote/pkgs/container/spokapilote-server)

**Analyser un PC, installer les bons pilotes. Depuis les sources officielles uniquement — d'un poste à tout un parc.**

Fini la tournée des sites constructeurs et les « driver updaters » douteux. SpokaPilote identifie chaque composant du
PC par son identifiant matériel exact, va chercher le pilote **chez Microsoft, chez le constructeur du PC ou chez le
fabricant de la carte graphique**, le vérifie (signature, compatibilité) puis l'installe — avec retour arrière possible.

## La famille Spoka

| Produit | Rôle |
|---|---|
| **SpokaPilote** (ce dépôt) | Pilotes de tout le poste : application Windows, ligne de commande `spokapilote`, version portable sur clé USB |
| **SpokaPilote Server** (ce dépôt, `server/`) | Console web facultative pour suivre et piloter le parc : santé, actions à distance, planification, alertes, équipe |
| **[SpokaPrint](https://github.com/Spokator/SpokaPrint)** | Imprimantes d'entreprise : découverte réseau, pilotes officiels vérifiés, profils de déploiement par site |

Les trois partagent le même moteur de confiance : sources officielles, signatures vérifiées, installation sûre.

## Trois façons de l'utiliser

### 1. Sur un poste
Installez `SpokaPilote-Setup-<version>.exe` ([Releases](https://github.com/Spokator/SpokaPilote/releases/latest) ;
`SpokaPilote-Installer-<version>-arm64.exe` pour les PC ARM64). Sur l'**Accueil**, un seul bouton : **Analyser ce
poste** (F5), puis **Installer N pilotes**. La note de santé (A à E) et l'état du BIOS disent tout de suite si le poste
va bien. Le reste — liste détaillée des pilotes, périphériques en erreur et réparation guidée, historique et
annulation, outils avancés — est à une page de distance (Ctrl+1 à Ctrl+6).

### 2. Avec la clé USB du technicien
Extrayez `SpokaPilote-Portable-<version>-x64.zip` sur une clé, rangez-y vos pilotes (`<clé>\SpokaPilote\Depot`, par
exemple les packs officiels Dell / HP / Lenovo / Surface de vos modèles). Sur n'importe quel PC : rien à installer,
fonctionne sans Internet, historique et sauvegardes rangés sur la clé, un dossier par poste.
→ [docs/MODE-PORTABLE.md](docs/MODE-PORTABLE.md)

### 3. Sur tout un parc, quelle que soit sa taille
1. **Dépôt de pilotes** sur un partage : pack officiel de chaque modèle (`spokapilote driverpack download`).
2. **Installation silencieuse** par GPO / Intune / RMM : `SpokaPilote-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`,
   avec un `settings.json` commun (dépôt, console, politique).
3. **Console centrale** (Docker) : chaque poste y remonte son état ; vous filtrez par groupe (site, modèle,
   sous-réseau…), lancez des actions à distance, planifiez (« pilotes manquants chaque lundi pour le groupe X »),
   recevez des alertes (e-mail, Teams, Slack, Discord) et gardez un journal d'audit par technicien.
4. **Politique de pilotes** : bloquer ou figer une version, limiter les sources ; liste de référence des pilotes
   connus pour provoquer des écrans bleus appliquée d'office.

→ [docs/CONSOLE-CENTRALE.md](docs/CONSOLE-CENTRALE.md) (dont la section « Grands parcs ») · [docs/POLITIQUE.md](docs/POLITIQUE.md) ·
[docs/PACKS-ET-IMAGES.md](docs/PACKS-ET-IMAGES.md)

## Pourquoi c'est « de confiance »

Une chaîne de contrôle stricte, **non désactivable** :

1. **Sources officielles uniquement** — Windows Update, Microsoft Update Catalog, catalogues Dell / HP / Lenovo,
   Microsoft Surface, NVIDIA, dépôt interne. Tout autre domaine est refusé (HTTPS obligatoire, redirections comprises).
2. **Empreinte et signature vérifiées** — le fichier doit correspondre à l'empreinte publiée, et être signé par un
   éditeur approuvé (Microsoft WHQL, constructeurs, fabricants de composants).
3. **Compatibilité réelle** — le pilote doit déclarer l'identifiant matériel exact, pour l'architecture et la version
   de Windows du poste.
4. **Filets de sécurité** — point de restauration, sauvegarde des pilotes, historique et annulation en un clic ;
   un pilote Windows qui fonctionne n'est jamais remplacé automatiquement ; un BIOS n'est jamais mis à jour sans
   demande explicite.

## Ce que ça couvre

| Sur chaque poste | Pour le parc |
|---|---|
| Pilotes manquants et mises à jour (dépôt → constructeur → NVIDIA → Windows Update → Catalog) | Console web : santé de chaque poste, matériel sans pilote sur tout le parc, historique |
| Note de santé A–E, pilotes dangereux déjà installés | Groupes de postes (règles automatiques), actions à distance, actions planifiées |
| État du BIOS (Dell, HP, Lenovo), signalé jamais flashé d'office | Comptes nominatifs et rôles, journal d'audit, alertes |
| Périphériques en erreur : diagnostic et réparation guidée | Politique de pilotes diffusée par la console, un partage ou une URL |
| Historique, annulation, sauvegardes réinstallables | Packs constructeur Dell / HP / Lenovo / Surface, pack hors ligne |
| Nettoyage des anciennes versions de pilotes | Injection de pilotes dans une image Windows (`install.wim`) |
| Maintenance planifiée sur le poste (compte SYSTEM) | Rapports HTML / JSON / CSV |
| Interface française ou anglaise, accessible (clavier, lecteurs d'écran) | Installation silencieuse, mise à jour automatique, version portable |

## Ligne de commande (extraits)
```powershell
spokapilote scan --html rapport.html                    # analyse (sans droits admin)
spokapilote install --yes                               # manquants + mises à jour (console admin)
spokapilote install --missing --exclude NVIDIA --yes    # choisir finement
spokapilote rollback last --yes                         # annuler la dernière intervention
spokapilote health                                      # note de santé et état du BIOS
spokapilote driverpack download \\srv\pilotes --model "Latitude 7420"
spokapilote schedule enable --weekly lundi --time 12:30 --mode missing
spokapilote agent enable                                # accepter les actions à distance de la console
spokapilote help                                        # toutes les commandes ; spokapilote <commande> --help
```
Codes de sortie : `0` OK · `1` erreur · `2` échec partiel · `64` commande invalide · `3010` redémarrage requis.
Les commandes `spokapilote printers …` restent disponibles pour les scripts existants ; pour les imprimantes,
utilisez désormais [SpokaPrint](https://github.com/Spokator/SpokaPrint).

## Documentation
- [Console centrale](docs/CONSOLE-CENTRALE.md) — installation Docker, comptes, groupes, planification, alertes, grands parcs
- [Mode portable](docs/MODE-PORTABLE.md) — clé USB du technicien
- [Boîte à outils du technicien](docs/OUTILS-TECHNICIEN.md) — santé, BIOS, réparation guidée, nettoyage du magasin
- [Politique de pilotes](docs/POLITIQUE.md) · [Packs constructeur et images](docs/PACKS-ET-IMAGES.md) ·
  [Pilotes graphiques](docs/PILOTES-GRAPHIQUES.md) · [Maintenance planifiée](docs/IMPRIMANTES-ET-PLANIFICATION.md)
- [Signature de code](docs/SIGNATURE.md) · [Distribution / winget](docs/DISTRIBUTION.md) · [Microsoft Store](docs/PUBLICATION-STORE.md)
- Site : https://spokator.github.io/SpokaPilote/

## Architecture

```
src/SpokaPilote.Core/         moteur partagé (aussi utilisé par SpokaPrint) : inventaire, sources, vérification,
                              installation, politique, santé, BIOS, dépôt, packs, console, mise à jour
src/SpokaPilote.Cli/          spokapilote.exe (Commands/ : une commande par fichier)
src/SpokaPilote.App/          SpokaPilote.App.exe (WPF .NET 10, thème Fluent ; Views/ : une page par fichier)
server/SpokaPilote.Server/    console centrale (ASP.NET Core, SQLite, Docker)
tests/                        tests du moteur et de la console
installer/ packaging/ scripts/  Inno Setup x64/ARM64, archives portables, MSIX, build et publication
```

## Construire

```powershell
dotnet test SpokaPilote.sln                 # tests
.\scripts\build-installer.ps1                # installeurs x64 + ARM64 et archives portables (contrôle de l'interface)
.\scripts\release.ps1                        # Release GitHub (notes du CHANGELOG) ; l'image Docker suit via GitHub Actions
```

## État et limites connues

- **Validé en réel** (Windows 11 25H2) : installeur et mise à jour automatique, installation de pilotes Windows Update
  et depuis un dépôt avec annulation, pack hors ligne, packs Dell / HP / Lenovo / Surface, NVIDIA (en simulation),
  politique de pilotes, console en Docker (actions à distance, alertes, comptes, groupes, planification), version
  portable, injection dans l'image officielle Windows 11 (machine virtuelle). État du BIOS contrôlé sur les catalogues
  officiels réels Dell, HP et Lenovo.
- **Pas encore validé sur matériel** : un vrai poste Dell / HP / Lenovo, une vraie clé USB, un PC ARM64,
  l'installation réelle d'un pilote NVIDIA.
- Intel et AMD (graphiques) ne publient pas de source exploitable automatiquement : SpokaPilote donne le lien officiel.
- Les installeurs ne sont signés que si un certificat est fourni ([docs/SIGNATURE.md](docs/SIGNATURE.md)).
