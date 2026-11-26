# Contribuer à SpokaPilote

Merci de votre intérêt ! Les signalements de bugs, les matériels non reconnus et les propositions sont les bienvenus.

## Signaler un problème

Ouvrez un ticket depuis l'onglet [Issues](https://github.com/Spokator/SpokaPilote/issues/new/choose) et choisissez
le bon modèle. Joignez si possible le rapport du poste : `spokapilote scan --json rapport.json` (vous pouvez y remplacer
le nom du poste). Une faille de sécurité ne se signale **jamais** dans un ticket public : voir [SECURITY.md](SECURITY.md).

## Proposer une modification

1. Pour un changement important, ouvrez d'abord un ticket pour en discuter.
2. Créez une branche, faites vos modifications, puis vérifiez :
   ```powershell
   dotnet build SpokaPilote.sln          # aucun avertissement
   dotnet test SpokaPilote.sln           # tous les tests passent
   .\scripts\build-installer.ps1 -Arch x64 -SkipTests   # si vous touchez l'interface ou l'installation
   ```
3. Ouvrez une pull request en décrivant le besoin et ce que vous avez testé.

## Principes à respecter

- **Sources officielles uniquement** : aucun téléchargement depuis un site tiers, aucune règle de contrôle (domaine,
  empreinte, signature, correspondance de l'INF) ne doit pouvoir être désactivée.
- **Aucune dépendance tierce** côté poste : uniquement .NET et les bibliothèques Microsoft.
- **Textes visibles en français**, clairs pour un non-spécialiste ; les messages d'erreur disent quoi faire.
- Scripts PowerShell compatibles **Windows PowerShell 5.1** (fichiers `.ps1` en UTF-8 avec BOM s'ils contiennent des
  accents ; tester les codes de sortie des exécutables plutôt que leur sortie d'erreur).
- Les noms des installeurs publiés (`SpokaPilote-Setup-<version>.exe`, `SpokaPilote-Installer-<version>-arm64.exe`) et
  l'`AppId` de l'installeur ne changent jamais : la mise à jour automatique et winget en dépendent.
- Une fonctionnalité nouvelle s'accompagne de tests (`tests/`) et, si besoin, d'une page dans `docs/` (publiée
  automatiquement sur le site).

## Code de conduite

Ce projet applique le [code de conduite](CODE_OF_CONDUCT.md) : restez courtois et bienveillants.

En contribuant, vous acceptez que votre contribution soit publiée sous la [licence MIT](LICENSE) du projet.
