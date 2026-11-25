# SpokaPilote

**Les bons pilotes, pour tout le poste et les imprimantes d'entreprise, depuis les sources officielles uniquement.**

Fini la tournée des sites constructeurs et les « driver updaters » douteux. SpokaPilote inventorie la machine,
identifie chaque composant par son identifiant matériel exact, va chercher le pilote **chez Microsoft ou chez le
constructeur du PC**, le vérifie, puis l'installe. Il fait de même pour les imprimantes et copieurs réseau.

| | |
|---|---|
| Interface | Application Windows 11 (thème Fluent), en français |
| Ligne de commande | `spokapilote` — pour scripts, RMM, Intune, GPO, clé USB |
| Plateformes | Windows 10 1809+ et Windows 11, x64 (ARM64 prévu) |
| Technologie | .NET 10 LTS, WPF, aucune dépendance tierce |
| Distribution | Installeur classique `SpokaPilote-Setup-<version>.exe` + Microsoft Store (MSIX signé par Microsoft) |

---

## Pourquoi c'est « de confiance »

Les sites agrégateurs redistribuent des pilotes dont on ignore la provenance. SpokaPilote applique au contraire
une chaîne de contrôle stricte, **non désactivable** :

1. **Sources officielles uniquement** — Windows Update, Microsoft Update Catalog, catalogues Dell / HP / Lenovo,
   et le dépôt interne de votre entreprise. Tout autre domaine est refusé (HTTPS obligatoire, redirections comprises).
2. **Empreinte vérifiée** — SHA-1 du catalogue Microsoft (contenu dans le nom du fichier), SHA-256 HP/Lenovo, MD5 Dell.
3. **Signature numérique** — vérifiée par l'API Windows `WinVerifyTrust`, et l'éditeur doit faire partie d'une liste
   approuvée (Microsoft WHQL, Dell, HP, Lenovo, Intel, AMD, NVIDIA, Realtek...).
4. **Compatibilité réelle** — l'INF doit déclarer l'identifiant matériel exact du périphérique, pour l'architecture
   et la version de Windows du poste. (Exemple réel : un paquet Kyocera du catalogue, dont le titre ne mentionne pas
   ARM64, a été correctement refusé sur un PC x64 au profit de sa variante x64.)
5. **Filets de sécurité** — point de restauration et sauvegarde des pilotes actuels avant toute installation ;
   un pilote Windows intégré qui fonctionne n'est jamais remplacé automatiquement.

## Ce que ça couvre

**Pilotes du poste** — sources interrogées en parallèle, la plus pertinente l'emporte pour chaque périphérique :

| Priorité | Source | Usage |
|---|---|---|
| 1 | Dépôt interne (dossier, clé USB, `\\serveur\pilotes`) | Pilotes validés par l'IT, hors ligne |
| 2 | Catalogue du constructeur : **Dell** (CatalogPC), **HP** (références HP Image Assistant), **Lenovo** (catalogue System Update) | Pilotes taillés pour le modèle exact |
| 3 | **Windows Update** (API officielle, contourne WSUS si besoin) | Pilotes WHQL ciblés |
| 4 | **Microsoft Update Catalog** (recherche par ID matériel) | Pilotes manquants que WU ne propose pas |

**Imprimantes et copieurs** — découverte réseau (ports 9100 / 631 / 515), identification du modèle exact par
SNMP (ID IEEE 1284), IPP ou page web, puis choix du pilote : déjà présent → dépôt interne → Microsoft (WHQL, modèle
exact puis universel du constructeur : HP UPD, Xerox GPD, Lexmark, Ricoh...) → **pilote IPP intégré à Windows**
(universel, sans téléchargement) → lien vers la page officielle. Création du port TCP/IP et de la file d'impression.

**Diagnostic** — les périphériques en erreur sans solution reçoivent une explication (ex. *« échec de descripteur USB :
problème matériel, câble ou port, aucun pilote ne peut le corriger »*).

**Historique et annulation** — chaque intervention est enregistrée ; « Annuler » retire les paquets ajoutés et les
périphériques reviennent à leur pilote précédent. Sauvegardes complètes réinstallables.

**Postes sans réseau** — pack hors ligne préparé sur un autre poste à partir du rapport JSON de la machine cible.

**Parc** — rapport consolidé de tous les postes : postes à traiter, matériel sans pilote sur tout le parc, modèles.

**Mise à jour automatique** — depuis les Releases GitHub, installeur vérifié (taille + SHA-256).

## Démarrage rapide

### Installation
Téléchargez `SpokaPilote-Setup-<version>.exe` depuis les [Releases](https://github.com/Spokator/SpokaPilote/releases/latest) et lancez-le : installation dans `Program Files`, raccourci dans le menu Démarrer
(et sur le Bureau en option), désinstallation depuis *Paramètres > Applications*. L'option cochée par défaut ajoute
la commande `spokapilote` à tous les terminaux. Installation silencieuse pour le parc :
`SpokaPilote-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

### Interface graphique
Lancez **SpokaPilote**, cliquez **Analyser ce poste**, vérifiez la liste dans **Pilotes**, puis
**Installer la sélection** (l'application propose de se relancer en administrateur).

### Ligne de commande
```powershell
spokapilote scan --html rapport.html                 # inventaire + pilotes proposés (sans droits admin)
spokapilote install --yes                            # installe manquants + mises à jour (console admin)
spokapilote install --missing --yes                  # uniquement les pilotes manquants
spokapilote install --dry-run                        # télécharge et vérifie tout, n'installe rien
spokapilote printers discover --drivers              # imprimantes du réseau + pilote recommandé
spokapilote printers install 10.0.1.25 --name "Copieur RDC" --yes
spokapilote printers find-driver "KYOCERA TASKalfa 3253ci" --verify
spokapilote history                                  # interventions passées
spokapilote rollback last --yes                      # annule la dernière intervention
spokapilote self-update --yes                        # met SpokaPilote à jour
```
Codes de sortie : `0` OK · `1` erreur · `2` échec partiel · `3010` redémarrage requis.
Journaux : `%ProgramData%\SpokaPilote\Logs` · rapports : `%ProgramData%\SpokaPilote\Reports`.

## Déployer un parc de 100 postes

1. **Préparez un poste de référence par modèle** (ex. Latitude 7420) : `spokapilote install --yes`, vérifiez que tout fonctionne.
2. **Exportez ses pilotes** vers un partage : `spokapilote export \\srv\pilotes`
   → `\\srv\pilotes\Dell_Inc.\Latitude_7420\Win11-amd64\`
3. **Déclarez le dépôt** sur le parc (une fois, ou via le fichier de configuration déployé par GPO/Intune) :
   `spokapilote config add-repo \\srv\pilotes`
4. **Sur chaque poste** (script de démarrage, RMM, Intune Win32, tâche planifiée) :
   ```powershell
   spokapilote install --yes --output-dir \\srv\rapports
   ```
   Le dépôt interne passe en priorité (rapide, hors ligne) ; ce qui manque est complété depuis le constructeur et Microsoft.
5. **Suivez le parc** : `spokapilote fleet-report \\srv\rapports` génère `parc.html` (postes à traiter, matériel sans
   pilote sur tout le parc, modèles). Aussi disponible dans l'interface, onglet *Dépôt de pilotes*.

### Poste sans réseau (pilote de carte réseau manquant)
```powershell
spokapilote scan --json E:\poste.json        # 1. sur le poste sans réseau (SpokaPilote portable sur la clé USB)
spokapilote pack E:\poste.json E:\pack        # 2. sur un poste connecté : télécharge et vérifie ses pilotes
spokapilote install --repo E:\pack --yes      # 3. de retour sur le poste sans réseau
```

Configuration centralisée : `%ProgramData%\SpokaPilote\settings.json` (dépôts, sources, domaines et éditeurs
supplémentaires autorisés, URL d'un catalogue d'imprimantes propre à l'organisation).

## Architecture

```
src/
  SpokaPilote.Core/          moteur (aucune dépendance UI)
    Inventory/               WMI : machine, périphériques, pilotes installés
    Matching/                analyseur INF + classement des correspondances façon Windows
    Sources/                 Windows Update (COM WUA), Microsoft Update Catalog, dépôt local
      Oem/                   Dell, HP, Lenovo
    Install/                 téléchargement contrôlé, WinVerifyTrust, pnputil, restauration
    Printers/                SNMP, IPP, découverte, résolution de pilote, installation
    Repository/              index du dépôt, export « poste de référence », import
    Engine/                  orchestration, fusion des sources, rapports HTML/JSON
  SpokaPilote.Cli/           spokapilote.exe
  SpokaPilote.App/           SpokaPilote.App.exe (WPF .NET 10, thème Fluent)
tests/SpokaPilote.Core.Tests 92 tests (INF, catalogue, SNMP/IPP, Dell/HP/Lenovo, confiance, fusion, parc, mise à jour)
installer/                   installeur Inno Setup (SpokaPilote.iss + images de l'assistant)
packaging/                   manifeste MSIX et visuels du Store
scripts/                     publish.ps1, build-installer.ps1, build-store.ps1, release.ps1
```

## Construire

```powershell
dotnet test                                        # tests
dotnet run --project src\SpokaPilote.App           # interface
dotnet run --project src\SpokaPilote.Cli -- scan   # CLI
.\scripts\build-installer.ps1                      # -> dist\SpokaPilote-Setup-<version>.exe
.\scripts\build-store.ps1                          # -> dist-store\SpokaPilote-<version>.msix (Store)
.\scripts\release.ps1                              # -> Release GitHub vX.Y.Z avec l'installeur (notes du CHANGELOG)
```
La version se règle à un seul endroit : `Directory.Build.props`. Les visuels (icône, Store, assistant
d'installation) se régénèrent avec `tools\make-assets.ps1`.

Publication sur le Microsoft Store : voir [docs/PUBLICATION-STORE.md](docs/PUBLICATION-STORE.md).

## État et limites connues (v1.0)

- **Validé en réel** (Gigabyte Z590, Windows 11 25H2) : installeur (installation, mise à jour, raccourcis, PATH,
  désinstallation), inventaire de ~200 périphériques, **installation réelle** de pilotes via Windows Update,
  installation depuis un dépôt puis **annulation** de l'intervention, catalogue Microsoft (téléchargement, SHA-1,
  extraction, signature WHQL, correspondance INF), pack hors ligne relu comme dépôt, rapport de parc, découverte
  réseau, pilotes d'imprimante Kyocera / HP / Xerox / Ricoh trouvés et vérifiés.
- **Testé sur les catalogues officiels réels, pas encore sur matériel** : Dell, HP, Lenovo — à confirmer sur un
  poste de chaque marque.
- **Pas encore testé** : l'ajout d'une file d'impression sur une vraie imprimante (aucune sur le réseau de développement).
- Le Microsoft Update Catalog limite le débit des recherches : SpokaPilote espace ses requêtes, met les résultats en
  cache 12 h, n'interroge pas le catalogue pour les pilotes Windows intégrés et, en ligne de commande, attend la fin
  d'une limitation plutôt que de rendre un résultat partiel.
- Canon, Konica Minolta, Sharp, Brother et Kyocera (pilotes universels) ne publient pas tous leurs pilotes chez
  Microsoft : pour ces marques, le pilote IPP Windows ou le dépôt interne prend le relais.
- L'installeur n'est pas signé (comme SpokaSoundBoard) : SmartScreen peut avertir au premier lancement.

## Feuille de route

- Console centrale web (inventaire du parc en temps réel, politique de pilotes approuvés)
- Pilotes graphiques et chipset Intel / AMD / NVIDIA via leurs catalogues officiels
- Support ARM64 complet, Microsoft Surface
- Injection de pilotes dans une image Windows hors ligne (WinPE / déploiement)
- Liste noire partagée de versions de pilotes problématiques