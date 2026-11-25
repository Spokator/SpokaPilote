# Pilotes graphiques : NVIDIA, Intel, AMD

Windows Update et les catalogues des constructeurs de PC proposent souvent des pilotes graphiques datés de
plusieurs mois. SpokaPilote interroge donc aussi les fabricants de cartes graphiques, avec la même chaîne de
confiance que pour les autres sources : sources officielles en HTTPS uniquement, signature Authenticode d'un éditeur
approuvé, et jamais de site tiers.

| Fabricant | Ce qui est automatique | Source |
|---|---|---|
| **NVIDIA** | Recherche, téléchargement, vérification **et installation silencieuse** | Service officiel de pilotes NVIDIA (celui de l'application NVIDIA) |
| **Intel** | Lien vers la bonne page officielle (candidat **informatif**) | intel.com (vérification manuelle) |
| **AMD** | Lien vers la page officielle (candidat **informatif**) | amd.com (vérification manuelle) |

Les sources s'activent quand le réglage `useGpuVendorSources` vaut `true` (par défaut) et qu'un adaptateur
d'affichage PCI du fabricant est présent : classe *Display*, ou code de classe PCI `CC_03` quand la carte n'a pas
encore de pilote. Identifiants reconnus : `PCI\VEN_10DE` (NVIDIA), `PCI\VEN_8086` (Intel) et `PCI\VEN_1002` (AMD).
Elles ont la même priorité que les catalogues Dell, HP et Lenovo, donc passent avant Windows Update pour la carte
concernée.

```powershell
spokapilote scan --sources gpu                       # NVIDIA + Intel + AMD seulement
spokapilote install --sources nvidia --dry-run --yes # télécharge et vérifie le pilote NVIDIA, n'installe rien
spokapilote install --nvidia-branch gameready --yes  # branche Game Ready pour cette exécution
spokapilote config set nvidiaBranch studio           # réglage permanent (studio | gameready)
spokapilote install --no-gpu --yes                   # sans les sites des fabricants de cartes graphiques
```

## NVIDIA

### Source

SpokaPilote utilise le service officiel `gfwsl.geforce.com`, méthode `getDispDrvrByDevid`. C'est le service
qu'interrogent GeForce Experience et l'application NVIDIA. On lui envoie **l'identifiant PCI de la carte**
(ex. `2F04` pour une RTX 5070), la version de Windows (`osC`, `osB`), l'architecture 64 bits et le format DCH.
Il renvoie le pilote recommandé. **Il n'y a donc pas de table de correspondance produit à maintenir** : le service
choisit lui-même le bon paquet. Comportement vérifié en réel :

| Carte | Paquet renvoyé |
|---|---|
| GeForce de bureau (RTX 5070, `2F04`) | `…-desktop-win10-win11-64bit-international-dch-whql.exe` |
| GeForce de portable (RTX 4060 Laptop, `28E0`) | `…-notebook-win10-win11-64bit-international-dch-whql.exe` |
| RTX professionnelle (RTX 4000 Ada, `27B2`) | `Quadro_Certified/…-quadro-rtx-desktop-notebook-…-dch-whql.exe` (pilote *RTX Enterprise*) |
| Ancienne GeForce (GTX 750 Ti, `1380`) | branche héritée *Security Update Driver* 582.78 |
| Carte inconnue | HTTP 404 → aucun candidat |

Le service de la page de téléchargement de nvidia.com (`AjaxDriverService.php?func=DriverManualLookup`,
`lookupValueSearch.aspx`) fonctionne lui aussi, mais il exige de convertir la carte en identifiants de série et de
produit (`psid`, `pfid`) à partir de son nom commercial. Cette correspondance par nom est fragile, c'est pourquoi
SpokaPilote ne l'utilise pas.

### Branche retenue pour un parc : Studio

| Branche | Rythme | Usage |
|---|---|---|
| **Studio** (défaut) | Moins fréquente, validée pour la stabilité avec les applications professionnelles | Postes de travail, parc |
| Game Ready | Toutes les 2 à 4 semaines, optimisée pour les nouveaux jeux | Postes de jeu (`nvidiaBranch = gameready`) |
| RTX Enterprise | Branche certifiée des cartes professionnelles RTX/Quadro | Servie automatiquement à ces cartes |

Le pilote Studio partage le même code que le Game Ready, mais il est publié moins souvent et testé pour la
stabilité : c'est le choix le plus prudent pour un parc. Les cartes RTX professionnelles et les anciennes GeForce
n'ont pas de pilote Studio (le service répond 404). SpokaPilote se rabat alors sur le pilote standard renvoyé pour
la carte : RTX Enterprise ou branche héritée. Les versions bêta et les aperçus sont toujours refusés.

### Versions

Windows affiche une version à quatre blocs. La version commerciale NVIDIA correspond à ses **5 derniers chiffres** :

| Version Windows | Version NVIDIA |
|---|---|
| 31.0.15.6094 | 560.94 |
| 32.0.16.1088 | 610.88 |
| 30.0.14.7212 | 472.12 |

La comparaison avec le pilote installé se fait sur la version NVIDIA. Le candidat n'est proposé que s'il est plus
récent, sauf si la carte est en erreur ou utilise la *Carte graphique de base Microsoft* : il devient alors
« Pilote manquant ». L'affichage indique les deux formats : `621.40 (Windows x.x.16.2140)`. Les deux premiers blocs
(ex. `32.0`) dépendent du modèle de pilote Windows et ne se déduisent pas de la version NVIDIA.

### Téléchargement et contrôles

- Paquet DCH « international » (toutes langues, dont le français) depuis `us.download.nvidia.com`. Ce domaine,
  déjà autorisé, est celui que le service renvoie pour les administrateurs (`DownloadURLAdmin`). Le miroir
  `international.download.nvidia.com`, qui héberge les mêmes paquets, est également autorisé. La variante « -g »
  réservée à l'application NVIDIA (`international-gfe.download.nvidia.com`) n'est jamais utilisée.
- **Taille** : lue par une requête HEAD et affichée, environ 1 Go (987,8 Mo pour la 621.40).
- **Empreinte** : NVIDIA ne publie pas d'empreinte officielle. Son stockage Akamai NetStorage expose toutefois le
  MD5 du fichier dans l'en-tête `ETag`. SpokaPilote l'utilise uniquement quand ce format est reconnu, comme contrôle
  d'intégrité du transfert, pas comme garantie de provenance.
- **Signature** : Authenticode valide, éditeur « NVIDIA Corporation » (déjà approuvé). C'est elle qui fonde la
  confiance.

### Installation silencieuse (pilote graphique seul)

1. **Extraction** : `"<paquet>.exe" -y -gm2 -nr -InstallPath="%TEMP%\SpokaPilote-nvidia_<id>"`.
   Le paquet est une archive 7-Zip auto-extractible, avec un module modifié par NVIDIA qui exige l'élévation.
   `-y -gm2` masque les dialogues et `-nr` extrait sans lancer l'installation. Contraintes vérifiées sur la 621.40 :
   en mode silencieux, la destination doit être **directement sous `%TEMP%` et ne pas exister**. Sinon,
   l'extracteur renvoie le code 8 (hors de `%TEMP%` ou dossier imbriqué) ou 12 (dossier existant). Le dossier est
   supprimé après l'installation.
2. **Installation** : `setup.exe -s -n Display.Driver -log:"%ProgramData%\SpokaPilote\Logs\nvidia" -loglevel:6`.
   Cette commande est celle du guide d'installation Windows de NVIDIA : `-s` rend l'installation silencieuse, `-n`
   évite le redémarrage forcé, et `Display.Driver` installe **uniquement le pilote graphique**, avec son panneau de
   configuration (intégré au paquet DCH). Ne sont pas installés : l'application NVIDIA / GeForce Experience,
   la télémétrie, ShadowPlay, PhysX et le pilote audio HDMI (le pilote audio déjà en place est conservé).
3. **Codes de sortie** documentés par NVIDIA : `0` succès, `1` succès avec redémarrage nécessaire (signalé, jamais
   imposé). Tout autre code est un échec.

Le test réel sur la RTX 5070 (pilote 32.0.16.1088 = 610.88) a porté sur la recherche (Studio 621.40, puis
Game Ready 621.40), puis sur `install --sources nvidia --dry-run` : téléchargement depuis `us.download.nvidia.com`,
MD5 concordant, signature « NVIDIA Corporation ». L'extraction a aussi été vérifiée avec la commande exacte (code 0,
`setup.exe` et `Display.Driver` présents). Le pilote n'a pas été installé.

### Limites

- Windows 10/11 **x64** uniquement : NVIDIA ne publie ni pilote ARM64 grand public, ni pilote 32 bits.
- Seul le format **DCH** est proposé. Un ancien pilote « Standard » demande une installation propre manuelle.
- Le service est celui de l'application NVIDIA. Il n'est pas documenté publiquement, mais il est stable et c'est
  celui de l'éditeur lui-même. En cas d'échec, l'erreur est signalée et Windows Update reste interrogé.
- Portables : NVIDIA a la même priorité que le catalogue du constructeur du PC. Si l'IT préfère le pilote validé
  par Dell, HP ou Lenovo, elle peut désactiver la source (`--no-gpu`) ou placer ce pilote dans le dépôt interne
  (priorité supérieure). Si l'installeur NVIDIA refuse une carte, son code d'échec est remonté tel quel.
- L'installation réelle (étape `setup.exe`) n'a pas été exécutée pendant les tests, pour ne pas modifier le poste.
  Les commutateurs et les codes de sortie sont ceux que documente NVIDIA.

## Intel (Arc, Iris Xe, UHD/HD)

**Candidat informatif**, non coché et jamais installé automatiquement. Il n'a pas de périphérique ciblé : il ne
masque donc pas les pilotes Intel installables proposés par Windows Update ou le catalogue Microsoft.

Pourquoi pas plus :
- le centre de téléchargement d'Intel (intel.com) renvoie **HTTP 403** aux clients non-navigateurs (protection
  anti-robots Akamai). Le contourner serait fragile et contraire à l'esprit de l'outil ;
- l'Assistant Intel (Intel Driver & Support Assistant) utilise un service local et un service distant
  **non documentés** ;
- aucun catalogue lisible par machine n'est publié. Les catalogues qui circulent sont tenus par des tiers, ce qui
  est exclu.

SpokaPilote oriente vers la bonne page officielle d'après le nom de la carte :

| Carte | Page |
|---|---|
| Arc, Iris Xe, UHD 7xx, « Intel Graphics » (Core Ultra) : 11e génération et suivantes | [Intel Arc Graphics – Windows](https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows.html) |
| HD, UHD 6xx, Iris Plus / Pro : 7e à 10e génération | [Intel 7th-10th Gen Processor Graphics – Windows](https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html) |
| Indéterminé (ex. « UHD Graphics » sans numéro) | [Intel Driver & Support Assistant](https://www.intel.com/content/www/us/en/support/detect.html) |

Déploiement manuel d'un pilote Intel téléchargé : `Installer.exe -s` (silencieux), `--overwrite` pour autoriser
une rétrogradation (article Intel 000006773, codes d'erreur : article 000091180). Sur un portable, le pilote du
constructeur du PC (Dell, HP, Lenovo) reste prioritaire : il est déjà proposé par les catalogues OEM.

Aucun adaptateur Intel n'est visible sur le poste de test (Z590 avec iGPU absent ou désactivé). La partie Intel
est donc couverte par les tests unitaires uniquement.

## AMD (Radeon)

**Candidat informatif** (non coché, jamais installé automatiquement, sans périphérique ciblé). AMD ne publie aucun
service ni catalogue lisible par machine. Son sélecteur de pilotes est une application web, et `drivers.amd.com`
refuse les téléchargements qui ne proviennent pas de ses pages (contrôle du référent). Plutôt que du scraping
fragile, SpokaPilote donne le lien officiel :

- cartes grand public : [AMD Software: Adrenalin Edition](https://www.amd.com/fr/support/download/drivers.html) ;
- Radeon PRO / FirePro : [AMD Software: PRO Edition](https://www.amd.com/fr/products/software/pro-edition.html),
  la branche certifiée pour les postes de travail.

Les pilotes qu'AMD publie sur Windows Update et le catalogue Microsoft restent installés automatiquement par ces
sources.

## Paramètres

| Clé (`settings.json`) | Défaut | Rôle |
|---|---|---|
| `useGpuVendorSources` | `true` | Active les trois sources (désactivable par `--no-gpu`) |
| `nvidiaBranch` | `studio` | `studio` ou `gameready` (option CLI `--nvidia-branch`) |
