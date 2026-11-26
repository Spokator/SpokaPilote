# Publier SpokaPilote sur le Microsoft Store

Le Store signe gratuitement le paquet MSIX : plus d'avertissement SmartScreen, mises à jour automatiques,
installation possible en entreprise via Intune / Microsoft Store pour entreprises / `winget install`.

## 1. Compte développeur

1. Créez un compte sur [Partner Center](https://partner.microsoft.com/dashboard/registration) (compte **individuel**
   gratuit, ou compte **société** si SpokaPilote doit être publié au nom d'une entreprise — vérification d'identité requise).
2. **Applications et jeux → Nouveau produit → Application MSIX ou PWA**, réservez le nom **SpokaPilote**.
3. Dans **Gestion des produits → Identité du produit**, notez :
   - `Package/Identity/Name` (ex. `12345Editeur.SpokaPilote`)
   - `Package/Identity/Publisher` (ex. `CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`)
   - `Package/Properties/PublisherDisplayName`

## 2. Construire le paquet

```powershell
.\scripts\build-store.ps1 -Arch all `
    -IdentityName "12345Editeur.SpokaPilote" `
    -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
    -PublisherDisplayName "Votre nom"
```

Résultat dans `dist-store\` :

| Fichier | Usage |
|---|---|
| `SpokaPilote-<version>.msixbundle` | **le fichier à soumettre** : x64 et ARM64 réunis, Windows installe la bonne architecture |
| `SpokaPilote-<version>-x64.msix`, `SpokaPilote-<version>-arm64.msix` | paquets séparés (soumission possible aussi, ou essais) |

Les paquets ne sont pas signés : c'est voulu, le Store les signe. La version vient de `Directory.Build.props` (suivie
de `.0`) et doit augmenter à chaque soumission. Le script vérifie que `SpokaPilote.App.exe` (application) et
`spokapilote.exe` (ligne de commande, alias `spokapilote`) déclarés dans le manifeste sont bien présents.
## 3. Capacités restreintes à justifier

Le manifeste déclare deux capacités restreintes. Dans la soumission (**Propriétés → Capacités restreintes**),
fournissez une justification. Texte proposé :

> **runFullTrust** : SpokaPilote est une application de bureau (WPF) de gestion de pilotes. Elle interroge WMI
> pour inventorier le matériel, utilise l'API Windows Update Agent et l'outil Microsoft pnputil pour installer
> des pilotes signés.
>
> **allowElevation** : l'installation de pilotes de périphériques et d'imprimantes est une opération système qui
> exige les droits administrateur. L'application démarre sans élévation (inventaire et recherche) et ne demande
> l'élévation UAC qu'à l'action explicite de l'utilisateur (« Installer »). Seuls des pilotes signés provenant de
> Microsoft (Windows Update / Microsoft Update Catalog) et des constructeurs (Dell, HP, Lenovo) sont installés,
> après vérification de leur signature numérique.

## 4. Fiche Store

- **Catégorie** : Outils de développement / Utilitaires et outils
- **Description courte** : *Pilotes de confiance pour tout le poste et les imprimantes d'entreprise, depuis les sources officielles uniquement.*
- **Captures** : `.\scripts\screenshots.ps1` (ou `.\scripts\build-store.ps1 -Screenshots`) analyse ce poste et capture
  chaque écran dans `dist-store\captures\` : les fichiers `store-*.png` (1600 × 900, format accepté par le Store)
  sont prêts à téléverser. Le nom du PC est remplacé par « PC-DEMO ».
- **Logo** : `packaging\Assets\StoreListing300.png` (300×300) ; régénérez les visuels avec `tools\make-assets.ps1`.
- **Politique de confidentialité** (URL obligatoire) : SpokaPilote ne collecte aucune donnée personnelle ; il contacte
  uniquement les serveurs de Microsoft et des constructeurs pour télécharger des catalogues et des pilotes.

## 5. Tester le paquet localement avant soumission

```powershell
.\scripts\build-store.ps1 -TestSign
```
Le script crée un certificat de test « CN=Spoka » dans votre magasin personnel et signe le paquet. Puis, dans une
console administrateur : importez `dist-store\SpokaPilote-test.cer` dans `Ordinateur local → Personnes autorisées`
(`certlm.msc`), et double-cliquez sur le `.msix`. La commande `spokapilote` devient disponible dans tous les terminaux
(alias d'exécution).

Après vos essais : désinstallez l'application, retirez le certificat de `Personnes autorisées`, et supprimez le
certificat de test (magasin personnel et sa copie dans « Autorités intermédiaires ») :
`Get-ChildItem Cert:\CurrentUser -Recurse | Where-Object Subject -eq "CN=Spoka" | Remove-Item`.

## Distribution hors Store (MSIX signé)

Avec votre propre certificat de signature de code ([SIGNATURE.md](SIGNATURE.md)) :

```powershell
.\scripts\build-store.ps1 -Arch all -Sign
```

L'éditeur du paquet est alors automatiquement le sujet de votre certificat (Windows l'exige). Les postes qui font
confiance à ce certificat installent le paquet par double-clic, Intune ou `Add-AppxPackage`.

## Installeur classique (hors Store)

`.\scripts\build-installer.ps1` produit `dist\SpokaPilote-Setup-<version>.exe` (x64) et
`dist\SpokaPilote-Installer-<version>-arm64.exe`. Ils sont signés automatiquement dès qu'une signature est configurée ;
sinon Windows SmartScreen peut afficher un avertissement au premier lancement. Les options (Azure Artifact Signing,
certificat OV…) et leurs coûts sont comparés dans [SIGNATURE.md](SIGNATURE.md).