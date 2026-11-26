# Distribution de SpokaPilote

Où et comment SpokaPilote est publié : GitHub Releases, mise à jour automatique, winget, Microsoft Store, image
Docker de la console et site web ; procédure de publication d'une version et de soumission à winget.

| Canal | Ce que reçoivent les utilisateurs | Comment c'est publié |
|---|---|---|
| **GitHub Releases** | `SpokaPilote-Setup-<version>.exe` (x64) et `SpokaPilote-Installer-<version>-arm64.exe` | `.\scripts\release.ps1` |
| **Mise à jour automatique** | les postes installés proposent la nouvelle version (vérification taille + SHA-256) | automatique dès la Release |
| **winget** | `winget install Spoka.SpokaPilote` | manifestes générés par `release.ps1`, **soumission manuelle** (ci-dessous) |
| **Microsoft Store** | installation et mises à jour par le Store, sans avertissement SmartScreen | [PUBLICATION-STORE.md](PUBLICATION-STORE.md) |
| **Console centrale** | image Docker `ghcr.io/spokator/spokapilote-server:<version>` et `:latest` | GitHub Actions, à chaque tag `vX.Y.Z` |
| **Site web** | <https://spokator.github.io/SpokaPilote/> (présentation, documentation, téléchargement) | GitHub Actions, à chaque modification de `site/` ou `docs/` sur `main` |

Les noms des installeurs ne doivent **jamais changer** : les postes en 1.0.x prennent le premier fichier « Setup*.exe »
d'une Release (d'où le nom `Installer` pour l'ARM64), les versions récentes cherchent « Setup » ou « Installer ».

## Publier une version

1. Augmentez la version dans `Directory.Build.props` et ajoutez une section `## <version>` en tête de `CHANGELOG.md`.
2. Commitez, puis lancez :
   ```powershell
   .\scripts\release.ps1
   ```
   Le script lance les tests, construit et vérifie les deux installeurs (signés si une signature est configurée :
   [SIGNATURE.md](SIGNATURE.md)), crée la Release avec les notes du CHANGELOG, puis génère les manifestes winget de la
   version dans `packaging\winget\<version>\` (adresses et empreintes SHA-256 des fichiers publiés) et les commite.
3. L'image Docker de la console est publiée automatiquement par GitHub Actions (onglet **Actions** du dépôt).

## winget : soumettre une version (quand vous le décidez)

winget est le gestionnaire de paquets intégré à Windows. Une fois SpokaPilote accepté, n'importe qui peut l'installer
avec `winget install Spoka.SpokaPilote`, et les administrateurs peuvent l'intégrer à leurs scripts ou à Intune.
Les manifestes sont prêts dans `packaging\winget\<version>\` ; rien n'est envoyé à Microsoft sans vous.

> L'identifiant **`Spoka.SpokaPilote`** est définitif une fois publié. Pour en choisir un autre, relancez
> `.\scripts\winget-manifest.ps1 -PackageIdentifier "Editeur.SpokaPilote"` avant la première soumission.

### Première fois

1. Installez l'outil officiel de Microsoft :
   ```powershell
   winget install -e --id Microsoft.WingetCreate
   ```
2. Créez un jeton GitHub (*Settings → Developer settings → Personal access tokens → Tokens (classic)*, case
   **public_repo**). wingetcreate s'en sert pour créer, depuis votre compte, une copie de `microsoft/winget-pkgs` et
   y ouvrir la demande d'ajout. Enregistrez-le une fois :
   ```powershell
   wingetcreate token --store --token <votre-jeton>
   ```

### À chaque version

1. Vérifiez les manifestes (déjà fait par le script, à refaire si vous les modifiez) :
   ```powershell
   winget validate --manifest packaging\winget\2.0.1
   ```
2. *(Facultatif)* Essayez l'installation exactement comme winget la fera — console **administrateur** ; le premier
   réglage autorise les manifestes locaux sur ce PC (`--disable` pour revenir en arrière) :
   ```powershell
   winget settings --enable LocalManifestFiles
   winget install --manifest packaging\winget\2.0.1
   ```
3. Soumettez :
   ```powershell
   wingetcreate submit --prtitle "New version: Spoka.SpokaPilote version 2.0.1" packaging\winget\2.0.1
   ```
   La commande affiche l'adresse de la demande ouverte sur `github.com/microsoft/winget-pkgs`.
4. Suivez la demande : des robots vérifient les manifestes et analysent les installeurs (antivirus, installation
   silencieuse dans une machine de test). À la **première** contribution, un robot demande d'accepter l'accord de
   contribution : répondez par le commentaire qu'il indique (`@microsoft-github-policy-service agree`). Un modérateur
   valide ensuite ; comptez de quelques heures à quelques jours. Si un robot ajoute une étiquette d'erreur, son
   commentaire explique quoi corriger : corrigez dans `packaging\winget\<version>\` et relancez `wingetcreate submit`.

Ensuite, `winget upgrade` proposera chaque nouvelle version soumise. Pour soumettre une version oubliée :
`.\scripts\winget-manifest.ps1 -Version <version>` puis l'étape 3.

### Notes

- Installeur Inno Setup : `InstallerType: inno`, installation **pour tous les utilisateurs** (`Scope: machine`, Program
  Files, élévation demandée par l'installeur), commutateurs silencieux `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
- `ProductCode` = clé de désinstallation créée par Inno Setup, c'est-à-dire l'`AppId` de `installer\SpokaPilote.iss`
  suivi de `_is1` : `{C4E1A7B2-5D3F-4A8E-9B6C-2F7D1E0A3B94}_is1`. winget reconnaît ainsi une installation existante,
  même faite avec l'installeur téléchargé. **Ne changez jamais l'AppId.**
- Description par défaut en français (`fr-FR`), traduction anglaise (`en-US`).
- winget accepte des installeurs non signés ; la signature ([SIGNATURE.md](SIGNATURE.md)) reste recommandée.

## Site web (GitHub Pages)

Le site est construit à partir de `site/` (pages) et de `docs/*.md` (documentation convertie en HTML) par
`scripts\build-site.ps1`, puis publié par le workflow `.github/workflows/pages.yml` à chaque modification de ces dossiers
sur `main`. Aperçu local :

```powershell
.\scripts\build-site.ps1          # -> dist-site\index.html (s'ouvre directement dans le navigateur)
```

Captures d'écran : `.\scripts\screenshots.ps1 -Site` les régénère à partir de l'application (nom du PC masqué).
